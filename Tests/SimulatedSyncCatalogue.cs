using System.Globalization;
using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

// The fixture supplies existing groups and protected items. Only the receiver's local track changes are simulated.
internal sealed class SimulatedSyncCatalogue : IDisposable
{
    internal const long ExistingTrackId = 101;
    internal const string ExistingTrackPath = "iTunes_Control/Music/F01/one.mp3";
    private const long ExistingItemId = 1;
    private const long DatabaseId = 1001;
    private const uint InitialAnchor = 7;
    private const long AlbumId = 1012;
    private const long ArtistId = 1011;
    private const long AlbumRowId = 12;
    private const long ArtistRowId = 11;
    private const long PerformerRowId = 15;
    private const long ComposerRowId = 14;
    private const long GenreId = 16;
    private const long MusicMediaType = 8;
    private const long SongMediaKind = 1;
    private const long LocalArtworkSource = 300;
    private const long TrackArtworkType = 1;
    private const long TrackEntityType = 0;
    private const long DefaultArtworkVariant = 0;
    private readonly CatalogueFixture fixture = new();

    public uint Anchor { get; private set; } = InitialAnchor;

    public SimulatedSyncCatalogue(bool keepExistingTrack)
    {
        this.fixture.Execute("""
            CREATE TABLE artwork(artwork_token TEXT,artwork_source_type INTEGER,artwork_type INTEGER,
              artwork_variant_type INTEGER,relative_path TEXT);
            CREATE TABLE artwork_token(entity_pid INTEGER,artwork_token TEXT,artwork_source_type INTEGER,
              artwork_type INTEGER,entity_type INTEGER,artwork_variant_type INTEGER);
            CREATE TABLE best_artwork_token AS SELECT * FROM artwork_token WHERE 0;
            """);
        if (!keepExistingTrack) this.Delete(ExistingTrackId);
    }

    public CatalogueSnapshot Snapshot() => this.fixture.Snapshot();

    public void SetExistingAudioSize(int size)
    {
        this.Execute("UPDATE item_extra SET file_size=$size WHERE item_pid=$id",
            ("size", size), ("id", ExistingItemId));
    }

    public void Apply(Dictionary<string, object> document)
    {
        var revision = checked((uint)document.Number(MediaKeys.Revision));
        Assert.That(revision, Is.EqualTo(this.Anchor + 1));
        using var transaction = this.fixture.Connection.BeginTransaction();
        foreach (var value in (object[])document[MediaKeys.Operations])
        {
            var operation = value.AsDict();
            switch (operation.Text(MediaKeys.Operation))
            {
                case MediaOperations.UpdateDatabase:
                    Assert.That(operation.Number(MediaKeys.PersistentId), Is.EqualTo(DatabaseId));
                    break;
                case MediaOperations.InsertTrack:
                    this.InsertTrack(operation);
                    break;
                case MediaOperations.UpdateTrack:
                    Assert.That(operation.Dictionary(MediaKeys.Item).Keys,
                        Is.EquivalentTo([MediaKeys.ArtworkCacheId, MediaKeys.ArtworkChecksum]));
                    break;
                case MediaOperations.DeleteTrack:
                    this.Delete(operation.Number(MediaKeys.PersistentId));
                    break;
                default:
                    Assert.Fail("Unexpected simulated receiver operation: " + operation.Text(MediaKeys.Operation));
                    break;
            }
        }

        this.Execute("UPDATE _MLDatabaseProperties SET value=$anchor",
            ("anchor", revision.ToString(CultureInfo.InvariantCulture)));
        transaction.Commit();
        this.Anchor = revision;
    }

    public void CompleteAudio(long id, string path, int size)
    {
        this.Insert("base_location", ("base_location_id", id), ("path", path[..path.LastIndexOf('/')]));
        this.Execute("UPDATE item SET base_location_id=$id WHERE item_pid=$id", ("id", id));
        this.Execute(
            "UPDATE item_extra SET location=$name,file_size=$size WHERE item_pid=$id",
            ("name", path[(path.LastIndexOf('/') + 1)..]),
            ("size", size),
            ("id", id));
    }

    public string CompleteArtwork(long id, long cacheId)
    {
        var itemId = id == ExistingTrackId ? ExistingItemId : id;
        var path = $"integration/{cacheId.ToString(CultureInfo.InvariantCulture)}.jpg";
        var token = cacheId.ToString(CultureInfo.InvariantCulture);
        this.Insert(
            "artwork",
            ("artwork_token", token),
            ("artwork_source_type", LocalArtworkSource),
            ("artwork_type", TrackArtworkType),
            ("artwork_variant_type", DefaultArtworkVariant),
            ("relative_path", path));
        foreach (var table in new[] { "artwork_token", "best_artwork_token" })
        {
            this.Execute($"DELETE FROM {table} WHERE entity_pid=$id", ("id", itemId));
            this.Insert(
                table,
                ("entity_pid", itemId),
                ("artwork_token", token),
                ("artwork_source_type", LocalArtworkSource),
                ("artwork_type", TrackArtworkType),
                ("entity_type", TrackEntityType),
                ("artwork_variant_type", DefaultArtworkVariant));
        }

        return path;
    }

    private void InsertTrack(Dictionary<string, object> operation)
    {
        var id = operation.Number(MediaKeys.PersistentId);
        var item = operation.Dictionary(MediaKeys.Item);
        var audio = operation.Dictionary(MediaKeys.Audio);
        Assert.That(item.Number(MediaKeys.AlbumId), Is.EqualTo(AlbumId));
        Assert.That(item.Number(MediaKeys.ArtistId), Is.EqualTo(ArtistId));
        Assert.That(item.Text(MediaKeys.Album), Is.EqualTo("Album"));
        Assert.That(item.Text(MediaKeys.AlbumArtist), Is.EqualTo("Artist"));
        Assert.That(item.Text(MediaKeys.Artist), Is.EqualTo("Artist"));
        Assert.That(item.Text(MediaKeys.Composer), Is.EqualTo("Composer"));
        Assert.That(item.Text(MediaKeys.Genre), Is.EqualTo("Genre"));
        this.Insert(
            "item",
            ("item_pid", id),
            ("media_type", MusicMediaType),
            ("remote_location_id", 0),
            ("base_location_id", 0),
            ("album_pid", AlbumRowId),
            ("album_artist_pid", ArtistRowId),
            ("item_artist_pid", PerformerRowId),
            ("composer_pid", ComposerRowId),
            ("genre_id", GenreId),
            ("track_number", item.GetValueOrDefault(MediaKeys.TrackNumber, 0L)),
            ("disc_number", item.GetValueOrDefault(MediaKeys.DiscNumber, 1L)),
            ("is_compilation", item.Flag(MediaKeys.IsCompilation) ? 1 : 0));
        this.Insert(
            "item_store",
            ("item_pid", id),
            ("sync_id", id),
            ("store_saga_id", 0));
        var milliseconds = Math.Floor(audio.Number(MediaKeys.Duration)
            * (double)TimeSpan.MillisecondsPerSecond / (double)audio[MediaKeys.SampleRate]);
        this.Insert(
            "item_extra",
            ("item_pid", id),
            ("title", item.Text(MediaKeys.Title)),
            ("location", null),
            ("file_size", 0),
            ("sort_title", item.Text(MediaKeys.SortTitle)),
            ("comment", item.GetValueOrDefault(MediaKeys.Comment)),
            ("track_count", item.GetValueOrDefault(MediaKeys.TrackCount, 0L)),
            ("disc_count", item.GetValueOrDefault(MediaKeys.DiscCount, 0L)),
            ("year", item.GetValueOrDefault(MediaKeys.Year, 0L)),
            ("total_time_ms", milliseconds),
            ("media_kind", SongMediaKind));
        this.Insert(
            "item_playback",
            ("item_pid", id),
            ("audio_format", audio.Number(MediaKeys.AudioFormat)),
            ("bit_rate", audio.Number(MediaKeys.BitRate)),
            ("sample_rate", audio[MediaKeys.SampleRate]),
            ("duration", audio.Number(MediaKeys.Duration)));
        this.Insert("item_stats", ("item_pid", id), ("remember_bookmark", 0));
    }

    private void Delete(long id)
    {
        var itemId = id == ExistingTrackId ? ExistingItemId : id;
        foreach (var table in new[] { "item", "item_store", "item_extra", "item_playback", "item_stats" })
        {
            this.Execute($"DELETE FROM {table} WHERE item_pid=$id", ("id", itemId));
        }

        foreach (var table in new[] { "artwork_token", "best_artwork_token" })
        {
            this.Execute($"DELETE FROM {table} WHERE entity_pid=$id", ("id", itemId));
        }
    }

    private void Insert(string table, params (string Column, object? Value)[] values)
    {
        var columns = string.Join(',', values.Select(value => value.Column));
        var parameters = string.Join(',', values.Select(value => "$" + value.Column));
        this.Execute($"INSERT INTO {table}({columns}) VALUES({parameters})", values);
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = this.fixture.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    public void Dispose() => this.fixture.Dispose();
}
