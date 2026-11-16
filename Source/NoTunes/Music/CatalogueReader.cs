using System.Globalization;
using System.Data.SQLite;
using NoTunes.Apple;

namespace NoTunes.Music;

internal static class CatalogueReader
{
    private const int MinimumSchemaVersion = 2350000;
    private const int MaximumTracks = 50000;
    private const long MusicMediaType = 8;
    private const int TitleColumn = 1;
    private const int BasePathColumn = 2;
    private const int FilenameColumn = 3;
    private const int FileSizeColumn = 4;
    private const int MusicTypeColumn = 5;
    private const int RemoteLocationColumn = 6;
    private const string ConfigureSql = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;";
    private const string IntegritySql = "PRAGMA quick_check";
    private const string IntegritySuccess = "ok";
    private const string VersionSql = "PRAGMA user_version";
    private const string IdentitySql = "SELECT db_pid,primary_container_pid FROM db_info";
    private const string AnchorSql = "SELECT value FROM _MLDatabaseProperties WHERE key='MLSyncClientSyncAnchor'";
    private const string ContainersSql = "SELECT sync_id FROM container WHERE sync_id<>0";
    private const string OccupiedIdsSql = """
        SELECT sync_id FROM item_store WHERE sync_id<>0
        UNION SELECT sync_id FROM composer WHERE sync_id<>0
        """;
    private const string AlbumsSql = "SELECT a.sync_id,a.album,ar.album_artist FROM album a LEFT JOIN album_artist ar USING(album_artist_pid)";
    private const string ArtistsSql = "SELECT sync_id,album_artist FROM album_artist";
    private const string AlbumSortsSql = """
        SELECT a.album,ar.album_artist,COALESCE(a.sort_album,a.album,'')
        FROM album a LEFT JOIN album_artist ar USING(album_artist_pid)
        """;
    private const string AlbumArtistSortsSql =
        "SELECT album_artist,COALESCE(sort_album_artist,album_artist,'') FROM album_artist";
    private const string ArtistSortsSql = "SELECT item_artist,COALESCE(sort_item_artist,item_artist,'') FROM item_artist";
    private const string ComposerSortsSql = "SELECT composer,COALESCE(sort_composer,composer,'') FROM composer";
    private const string TracksSql = """
        SELECT s.sync_id, e.title, b.path, e.location, e.file_size, i.media_type, i.remote_location_id
        FROM item i JOIN item_store s USING(item_pid)
        LEFT JOIN item_extra e USING(item_pid) LEFT JOIN base_location b USING(base_location_id)
        WHERE s.sync_id<>0 AND s.store_saga_id=0 ORDER BY s.sync_id
        """;

    public static async Task<DeviceCatalogue> ReadAsync(CatalogueSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var workspace = new SyncWorkspace();
        var path = Path.Combine(workspace.Root, SyncPaths.DatabaseFile);
        await File.WriteAllBytesAsync(path, snapshot.Database, cancellationToken);
        if (snapshot.Wal is not null)
            await File.WriteAllBytesAsync(path + SyncPaths.WalSuffix, snapshot.Wal, cancellationToken);
        try
        {
            // SQLite replays only committed WAL frames in our private local copy. Never open the phone's file.
            await using var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                FailIfMissing = true,
                Pooling = false
            }.ToString());
            connection.Open();
            Execute(connection, ConfigureSql);
            if (!StringComparer.Ordinal.Equals(Scalar(connection, IntegritySql), IntegritySuccess))
                throw new InvalidDataException("The captured music catalogue failed SQLite integrity checks.");
            if (long.Parse(Scalar(connection, VersionSql), CultureInfo.InvariantCulture) < MinimumSchemaVersion)
                throw new InvalidDataException("Unsupported iPhone music catalogue schema.");

            await using var ids = connection.CreateCommand();
            ids.CommandText = IdentitySql;
            await using var rows = ids.ExecuteReader();
            if (!rows.Read()) throw new IOException("The music library has not been initialized by a sync host.");

            var databaseId = rows.GetInt64(0);
            var containerId = rows.GetInt64(1);
            if (databaseId == 0 || containerId == 0 || rows.Read())
                throw new InvalidDataException("Ambiguous music database identity.");

            rows.Close();
            var anchorText = Scalar(connection, AnchorSql, SyncProtocol.InitialMediaAnchor);
            if (!uint.TryParse(
                anchorText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var anchor)
                || anchor == uint.MaxValue || anchor.ToString(CultureInfo.InvariantCulture) != anchorText)
                throw new InvalidDataException("Invalid Media sync anchor.");

            var occupied = new HashSet<long> { databaseId, containerId };
            await using (var occupiedCommand = connection.CreateCommand())
            {
                occupiedCommand.CommandText = OccupiedIdsSql;
                await using var occupiedRows = occupiedCommand.ExecuteReader();
                while (occupiedRows.Read())
                    occupied.Add(occupiedRows.GetInt64(0));
            }

            var artists = ReadGroups(connection, ArtistsSql, occupied);
            var albums = ReadGroups(
                connection,
                AlbumsSql,
                occupied,
                true);

            await using var command = connection.CreateCommand();
            command.CommandText = TracksSql;
            await using var reader = command.ExecuteReader();
            var tracks = new List<DeviceTrack>();
            var trackIds = new HashSet<long>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tracks.Count >= MaximumTracks) throw new InvalidDataException("Catalogue track limit exceeded.");

                var id = reader.GetInt64(0);
                occupied.Add(id);
                // Preserve cloud purchases, videos and other nonlocal/nonmusic rows.
                if (reader.GetInt64(MusicTypeColumn) != MusicMediaType || reader.GetInt64(RemoteLocationColumn) != 0)
                    continue;
                var fullPath = reader.GetString(BasePathColumn) + "/" + reader.GetString(FilenameColumn);
                AfcClient.ValidatePath(fullPath);
                if (!fullPath.StartsWith(SyncPaths.MusicBucketPrefix, StringComparison.Ordinal)
                    || !trackIds.Add(id) || !paths.Add(fullPath) || reader.GetInt64(FileSizeColumn) < 0)
                    throw new InvalidDataException("Unsafe or ambiguous music catalogue entry.");

                tracks.Add(new DeviceTrack(
                    id,
                    reader.GetString(TitleColumn),
                    fullPath,
                    reader.GetInt64(FileSizeColumn)));
            }

            await using var containers = connection.CreateCommand();
            containers.CommandText = ContainersSql;
            await using var containerRows = containers.ExecuteReader();
            while (containerRows.Read())
                occupied.Add(containerRows.GetInt64(0));
            return new DeviceCatalogue(
                databaseId,
                containerId,
                anchor,
                tracks,
                albums,
                artists,
                occupied)
            {
                SortNames = new GroupSortNames(
                    ReadSortNames(connection, AlbumSortsSql, true),
                    ReadSortNames(connection, AlbumArtistSortsSql),
                    ReadSortNames(connection, ArtistSortsSql),
                    ReadSortNames(connection, ComposerSortsSql))
            };
        }
        catch (Exception e) when (e is SQLiteException or InvalidCastException or FormatException)
        {
            throw new InvalidDataException("Cannot read the captured iPhone music catalogue: " + e.Message, e);
        }
    }

    private static Dictionary<string, long> ReadGroups(
        SQLiteConnection connection,
        string sql,
        HashSet<long> occupied,
        bool album = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, long>(GroupNameComparer.Instance);
        while (rows.Read())
        {
            var id = rows.GetInt64(0);
            if (id == 0)
                continue;
            occupied.Add(id);
            var name = rows.IsDBNull(1) ? string.Empty : rows.GetString(1);
            var key = album ? GroupKey(name, rows.IsDBNull(2) ? string.Empty : rows.GetString(2)) : name;
            if (!result.TryAdd(key, id) && result[key] != id)
                result[key] = 0; // Duplicate labels need a fresh identity; never choose an arbitrary group.
        }

        return result;
    }

    internal static string GroupKey(string album, string artist)
    {
        var albumKey = album.All(char.IsAscii) ? album.ToUpperInvariant() : album;
        var artistKey = artist.All(char.IsAscii) ? artist.ToUpperInvariant() : artist;
        return albumKey + "\0" + artistKey;
    }

    private static Dictionary<string, string?> ReadSortNames(SQLiteConnection connection, string sql, bool album = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, string?>(GroupNameComparer.Instance);
        while (rows.Read())
        {
            var name = rows.IsDBNull(0) ? string.Empty : rows.GetString(0);
            var key = album ? GroupKey(name, rows.IsDBNull(1) ? string.Empty : rows.GetString(1)) : name;
            var sort = rows.GetString(album ? 2 : 1);
            if (!result.TryAdd(key, sort) && !StringComparer.Ordinal.Equals(result[key], sort))
                result[key] = null;
        }

        return result;
    }

    private static string Scalar(SQLiteConnection connection, string sql, string? missing = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null ? missing ?? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(SQLiteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
