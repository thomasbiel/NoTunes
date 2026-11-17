using System.Globalization;
using System.Data.Common;
using System.Data.SQLite;
using NoTunes.Apple;

namespace NoTunes.Music;

internal static class CatalogueReader
{
    private const int MinimumSchemaVersion = 2350000;
    internal const int MaximumTracks = 50000;
    private const long MusicMediaType = 8;
    private const int SyncIdColumn = 0;
    private const int TitleColumn = 1;
    private const int BasePathColumn = 2;
    private const int FilenameColumn = 3;
    private const int FileSizeColumn = 4;
    private const int MusicTypeColumn = 5;
    private const int RemoteLocationColumn = 6;
    private const int BaseLocationIdColumn = 7;
    private const int ItemIdColumn = 8;
    private const int StoreSagaIdColumn = 9;
    private const int DatabaseIdColumn = 0;
    private const int ContainerIdColumn = 1;
    private const int ContainerRowIdColumn = 1;
    private const int GroupIdColumn = 0;
    private const int GroupNameColumn = 1;
    private const int GroupAlbumArtistColumn = 2;
    private const int SortNameColumn = 0;
    private const int SortArtistColumn = 1;
    private const int AlbumSortValueColumn = 2;
    private const int OtherSortValueColumn = 1;
    internal const char GroupKeySeparator = '\0';
    private const long MissingBaseLocationId = 0;
    private const string ConfigureSql = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;";
    private const string IntegritySql = "PRAGMA quick_check";
    private const string IntegritySuccess = "ok";
    private const string VersionSql = "PRAGMA user_version";
    private const string IdentitySql = "SELECT db_pid,primary_container_pid FROM db_info";
    private const string AnchorSql = "SELECT value FROM _MLDatabaseProperties WHERE key='MLSyncClientSyncAnchor'";
    private const string ContainersSql = "SELECT sync_id,container_pid FROM container";
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
    private const string ItemsSql = """
        SELECT s.sync_id, e.title, b.path, e.location, e.file_size, i.media_type, i.remote_location_id,
               i.base_location_id, i.item_pid, s.store_saga_id,
               i.album_pid AS AlbumRowId, i.album_artist_pid AS AlbumArtistRowId,
               i.item_artist_pid AS ItemArtistRowId, i.composer_pid AS ComposerRowId, i.genre_id AS GenreId,
               a.sync_id AS AlbumId, ar.sync_id AS AlbumArtistId,
               a.album AS Album, ar.album_artist AS AlbumArtist, ia.item_artist AS Artist,
               c.composer AS Composer, g.genre AS Genre,
               e.sort_title AS SortTitle, e.comment AS Comment,
               COALESCE(a.sort_album,a.album) AS SortAlbum,
               COALESCE(ar.sort_album_artist,ar.album_artist) AS SortAlbumArtist,
               COALESCE(ia.sort_item_artist,ia.item_artist) AS SortArtist,
               COALESCE(c.sort_composer,c.composer) AS SortComposer,
               i.track_number AS TrackNumber, e.track_count AS TrackCount,
               i.disc_number AS DiscNumber, e.disc_count AS DiscCount, e.year AS Year,
               i.is_compilation AS Compilation, e.total_time_ms AS TotalMilliseconds, e.media_kind AS MediaKind,
               p.audio_format AS AudioFormat, p.bit_rate AS BitRate, p.sample_rate AS SampleRate,
               p.duration AS Duration, st.remember_bookmark AS RememberBookmark
        FROM item i LEFT JOIN item_store s USING(item_pid)
        LEFT JOIN item_extra e USING(item_pid) LEFT JOIN base_location b USING(base_location_id)
        LEFT JOIN album a ON i.album_pid=a.album_pid
        LEFT JOIN album_artist ar ON i.album_artist_pid=ar.album_artist_pid
        LEFT JOIN item_artist ia ON i.item_artist_pid=ia.item_artist_pid
        LEFT JOIN composer c ON i.composer_pid=c.composer_pid
        LEFT JOIN genre g ON i.genre_id=g.genre_id
        LEFT JOIN item_playback p USING(item_pid) LEFT JOIN item_stats st USING(item_pid)
        ORDER BY s.sync_id,i.item_pid
        """;

    public static async Task<DeviceCatalogue> ReadAsync(
        CatalogueSnapshot snapshot,
        CancellationToken cancellationToken,
        bool allowUninitialized = false)
    {
        using var workspace = new SyncWorkspace();
        var path = Path.Combine(workspace.Root, SyncPaths.DatabaseFile);
        await File.WriteAllBytesAsync(path, snapshot.Database, cancellationToken);
        if (snapshot.Wal is not null)
        {
            await File.WriteAllBytesAsync(path + SyncPaths.WalSuffix, snapshot.Wal, cancellationToken);
        }

        try
        {
            // SQLite replays only committed WAL frames in our private local copy. Never open the phone's file.
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                FailIfMissing = true,
                Pooling = false
            };

            await using var connection = new SQLiteConnection(builder.ToString());

            connection.Open();
            Execute(connection, ConfigureSql);

            if (!StringComparer.Ordinal.Equals(Scalar(connection, IntegritySql), IntegritySuccess))
                throw new InvalidDataException("The captured music catalogue failed SQLite integrity checks.");
            if (long.Parse(Scalar(connection, VersionSql), CultureInfo.InvariantCulture) < MinimumSchemaVersion)
                throw new InvalidDataException("Unsupported iPhone music catalogue schema.");

            await using var ids = connection.CreateCommand();
            ids.CommandText = IdentitySql;
            await using var rows = ids.ExecuteReader();
            var initialized = rows.Read();
            if (!initialized && !allowUninitialized)
                throw new IOException("The music library has not been initialized. Run 'notunes init' first.");

            var databaseId = initialized ? rows.GetInt64(DatabaseIdColumn) : 0;
            var containerId = initialized ? rows.GetInt64(ContainerIdColumn) : 0;
            if (initialized && (databaseId == 0 || containerId == 0 || rows.Read()))
            {
                throw new InvalidDataException("Ambiguous music database identity.");
            }

            rows.Close();
            var anchorText = Scalar(connection, AnchorSql, SyncProtocol.InitialMediaAnchor);
            if (!uint.TryParse(
                anchorText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var anchor) || anchor == uint.MaxValue || anchor.ToString(CultureInfo.InvariantCulture) != anchorText)
            {
                throw new InvalidDataException("Invalid Media sync anchor.");
            }

            var occupied = new HashSet<long> { databaseId, containerId };
            await using (var occupiedCommand = connection.CreateCommand())
            {
                occupiedCommand.CommandText = OccupiedIdsSql;
                await using var occupiedRows = occupiedCommand.ExecuteReader();
                while (occupiedRows.Read())
                {
                    occupied.Add(occupiedRows.GetInt64(SyncIdColumn));
                }
            }

            var artists = ReadGroups(connection, ArtistsSql, occupied);
            var albums = ReadGroups(
                connection,
                AlbumsSql,
                occupied,
                true);

            await using var command = connection.CreateCommand();
            command.CommandText = ItemsSql;
            await using var reader = command.ExecuteReader();
            var tracks = new List<DeviceTrack>();
            var protectedItems = new List<ProtectedCatalogueItem>();
            var trackIds = new HashSet<long>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = OptionalNumber(reader, SyncIdColumn);
                var sagaId = OptionalNumber(reader, StoreSagaIdColumn);
                var mediaType = OptionalNumber(reader, MusicTypeColumn);
                var remoteLocation = OptionalNumber(reader, RemoteLocationColumn);
                if (id is { } occupiedId && occupiedId != 0)
                {
                    if (!trackIds.Add(occupiedId))
                        throw new InvalidDataException("Ambiguous track sync identity in the music catalogue.");

                    occupied.Add(occupiedId);
                }

                // Preserve cloud purchases, videos and other nonlocal/nonmusic rows.
                if (id is null or 0 || sagaId != 0 || mediaType != MusicMediaType || remoteLocation != 0)
                {
                    protectedItems.Add(new ProtectedCatalogueItem(
                        reader.GetInt64(ItemIdColumn),
                        id,
                        sagaId,
                        mediaType,
                        remoteLocation,
                        OptionalText(reader, TitleColumn),
                        OptionalText(reader, BasePathColumn),
                        OptionalText(reader, FilenameColumn),
                        OptionalNumber(reader, FileSizeColumn))
                    {
                        Metadata = ReadTrackMetadata(reader)
                    });
                    continue;
                }

                if (tracks.Count >= MaximumTracks) throw new InvalidDataException("Catalogue track limit exceeded.");

                var size = reader.GetInt64(FileSizeColumn);
                var incomplete = reader.GetInt64(BaseLocationIdColumn) == MissingBaseLocationId && size == 0
                    && (reader.IsDBNull(FilenameColumn) || reader.GetString(FilenameColumn).Length == 0);

                string? fullPath = null;
                if (!incomplete)
                {
                    fullPath = reader.GetString(BasePathColumn) + "/" + reader.GetString(FilenameColumn);
                    AfcClient.ValidatePath(fullPath);
                    if (!fullPath.StartsWith(SyncPaths.MusicBucketPrefix, StringComparison.Ordinal) || !paths.Add(fullPath))
                    {
                        throw new InvalidDataException("Unsafe or ambiguous music catalogue entry.");
                    }
                }

                if (size < 0)
                    throw new InvalidDataException("Unsafe or ambiguous music catalogue entry.");

                tracks.Add(new DeviceTrack(
                    id.Value,
                    reader.GetString(TitleColumn),
                    fullPath,
                    size)
                {
                    Metadata = ReadTrackMetadata(reader)
                });
            }

            await using var containers = connection.CreateCommand();
            containers.CommandText = ContainersSql;
            await using var containerRows = containers.ExecuteReader();
            var hasPrimaryContainer = false;
            while (containerRows.Read())
            {
                var id = containerRows.GetInt64(SyncIdColumn);
                if (id != 0) occupied.Add(id);

                hasPrimaryContainer |= containerRows.GetInt64(ContainerRowIdColumn) == containerId;
            }

            if (initialized && !hasPrimaryContainer)
                throw new IOException("The music library is incomplete: its primary container is missing.");

            return ArtworkCatalogue.Read(connection, new DeviceCatalogue(
                databaseId,
                containerId,
                anchor,
                tracks,
                albums,
                artists,
                occupied)
            {
                ProtectedItems = protectedItems,
                SortNames = new GroupSortNames(
                    ReadSortNames(connection, AlbumSortsSql, true),
                    ReadSortNames(connection, AlbumArtistSortsSql),
                    ReadSortNames(connection, ArtistSortsSql),
                    ReadSortNames(connection, ComposerSortsSql))
            });
        }
        catch (Exception e) when (e is SQLiteException or InvalidCastException or FormatException)
        {
            throw new InvalidDataException("Cannot read the captured iPhone music catalogue: " + e.Message, e);
        }
    }

    private static long? OptionalNumber(DbDataReader reader, int column)
    {
        return reader.IsDBNull(column) ? null : reader.GetInt64(column);
    }

    private static string? OptionalText(DbDataReader reader, int column)
    {
        return reader.IsDBNull(column) ? null : reader.GetString(column);
    }

    private static TrackMetadata ReadTrackMetadata(DbDataReader reader)
    {
        return new TrackMetadata
        {
            AlbumRowId = Number(nameof(TrackMetadata.AlbumRowId)),
            AlbumArtistRowId = Number(nameof(TrackMetadata.AlbumArtistRowId)),
            ItemArtistRowId = Number(nameof(TrackMetadata.ItemArtistRowId)),
            ComposerRowId = Number(nameof(TrackMetadata.ComposerRowId)),
            GenreId = Number(nameof(TrackMetadata.GenreId)),
            AlbumId = Number(nameof(TrackMetadata.AlbumId)),
            AlbumArtistId = Number(nameof(TrackMetadata.AlbumArtistId)),
            Album = Name(nameof(TrackMetadata.Album)),
            AlbumArtist = Name(nameof(TrackMetadata.AlbumArtist)),
            Artist = Name(nameof(TrackMetadata.Artist)),
            Composer = Name(nameof(TrackMetadata.Composer)),
            Genre = Name(nameof(TrackMetadata.Genre)),
            SortTitle = Text(nameof(TrackMetadata.SortTitle)),
            Comment = Text(nameof(TrackMetadata.Comment)),
            SortAlbum = Text(nameof(TrackMetadata.SortAlbum)),
            SortAlbumArtist = Text(nameof(TrackMetadata.SortAlbumArtist)),
            SortArtist = Text(nameof(TrackMetadata.SortArtist)),
            SortComposer = Text(nameof(TrackMetadata.SortComposer)),
            TrackNumber = Number(nameof(TrackMetadata.TrackNumber)),
            TrackCount = Number(nameof(TrackMetadata.TrackCount)),
            DiscNumber = Number(nameof(TrackMetadata.DiscNumber)),
            DiscCount = Number(nameof(TrackMetadata.DiscCount)),
            Year = Number(nameof(TrackMetadata.Year)),
            Compilation = Number(nameof(TrackMetadata.Compilation)),
            TotalMilliseconds = Real(nameof(TrackMetadata.TotalMilliseconds)),
            MediaKind = Number(nameof(TrackMetadata.MediaKind)),
            AudioFormat = Number(nameof(TrackMetadata.AudioFormat)),
            BitRate = Number(nameof(TrackMetadata.BitRate)),
            SampleRate = Real(nameof(TrackMetadata.SampleRate)),
            Duration = Number(nameof(TrackMetadata.Duration)),
            RememberBookmark = Number(nameof(TrackMetadata.RememberBookmark))
        };

        long? Number(string column) => OptionalNumber(reader, reader.GetOrdinal(column));

        string? Text(string column) => OptionalText(reader, reader.GetOrdinal(column));

        string? Name(string column) => Text(column) is { } text ? GroupNameComparer.Normalize(text) : null;

        double? Real(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index) ? null : reader.GetDouble(index);
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
        var groupIds = new HashSet<long>();
        while (rows.Read())
        {
            var id = rows.GetInt64(GroupIdColumn);
            if (id == 0) continue;

            if (!groupIds.Add(id))
                throw new InvalidDataException("Ambiguous group sync identity in the music catalogue.");

            occupied.Add(id);
            var name = rows.IsDBNull(GroupNameColumn) ? string.Empty : rows.GetString(GroupNameColumn);
            var key = album ? GroupKey(name,
                rows.IsDBNull(GroupAlbumArtistColumn) ? string.Empty : rows.GetString(GroupAlbumArtistColumn)) : name;
            if (!result.TryAdd(key, id) && result[key] != id)
            {
                result[key] = 0; // Duplicate labels need a fresh identity; never choose an arbitrary group.
            }
        }

        return result;
    }

    internal static string GroupKey(string album, string artist)
    {
        var albumKey = GroupNameComparer.Normalize(album);
        var artistKey = GroupNameComparer.Normalize(artist);
        return albumKey + GroupKeySeparator + artistKey;
    }

    private static Dictionary<string, string?> ReadSortNames(SQLiteConnection connection, string sql, bool album = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, string?>(GroupNameComparer.Instance);
        while (rows.Read())
        {
            var name = rows.IsDBNull(SortNameColumn) ? string.Empty : rows.GetString(SortNameColumn);
            var key = album ? GroupKey(name,
                rows.IsDBNull(SortArtistColumn) ? string.Empty : rows.GetString(SortArtistColumn)) : name;
            var sort = rows.GetString(album ? AlbumSortValueColumn : OtherSortValueColumn);
            if (!result.TryAdd(key, sort) && !StringComparer.Ordinal.Equals(result[key], sort))
            {
                result[key] = null;
            }
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
