using System.Data.SQLite;
using NoTunes.Apple;
using NoTunes.Music;

namespace NoTunes.Tests;

internal sealed class CatalogueFixture : IDisposable
{
    private const string ArtworkSchema = """
        CREATE TABLE artwork(artwork_token TEXT,artwork_source_type INTEGER,relative_path TEXT,
            artwork_type INTEGER,artwork_variant_type INTEGER);
        CREATE TABLE artwork_token(entity_pid INTEGER,artwork_token TEXT,artwork_source_type INTEGER,
            artwork_type INTEGER,entity_type INTEGER,artwork_variant_type INTEGER);
        CREATE TABLE best_artwork_token(entity_pid INTEGER,entity_type INTEGER,artwork_type INTEGER,
            available_artwork_token TEXT,fetchable_artwork_token TEXT,fetchable_artwork_source_type INTEGER,
            artwork_variant_type INTEGER);
        """;

    private const string Schema = """
        PRAGMA user_version=2410000;
        CREATE TABLE db_info(db_pid INTEGER,primary_container_pid INTEGER);
        INSERT INTO db_info VALUES(1001,1002);
        CREATE TABLE _MLDatabaseProperties(key TEXT,value TEXT);
        INSERT INTO _MLDatabaseProperties VALUES('MLSyncClientSyncAnchor','7');
        CREATE TABLE item(item_pid INTEGER,media_type INTEGER,remote_location_id INTEGER,base_location_id INTEGER);
        CREATE TABLE item_store(item_pid INTEGER,sync_id INTEGER,store_saga_id INTEGER DEFAULT 0);
        CREATE TABLE item_extra(item_pid INTEGER,title TEXT,location TEXT,file_size INTEGER);
        CREATE TABLE base_location(base_location_id INTEGER,path TEXT);
        INSERT INTO base_location VALUES(1,'iTunes_Control/Music/F01');
        CREATE TABLE album_artist(album_artist_pid INTEGER,sync_id INTEGER,album_artist TEXT,sort_album_artist TEXT);
        INSERT INTO album_artist VALUES(11,1011,'Artist','Artist, The');
        CREATE TABLE album(album_pid INTEGER,sync_id INTEGER,album TEXT,album_artist_pid INTEGER,sort_album TEXT);
        INSERT INTO album VALUES(12,1012,'Album',11,'Album, The');
        CREATE TABLE container(sync_id INTEGER,container_pid INTEGER);
        INSERT INTO container VALUES(1002,1002);
        CREATE TABLE composer(sync_id INTEGER,composer TEXT,sort_composer TEXT);
        INSERT INTO composer VALUES(1014,'Composer','Composer, The');
        CREATE TABLE item_artist(item_artist TEXT,sort_item_artist TEXT);
        INSERT INTO item_artist VALUES('Artist','Performer, The');
        INSERT INTO item VALUES(1,8,0,1),(2,8,0,1),(3,2,0,1),(4,8,1,1),(5,8,0,1);
        INSERT INTO item_store VALUES(1,101,0),(2,0,999),(3,103,0),(4,104,0),(5,105,555);
        INSERT INTO item_extra VALUES(1,'Title','one.mp3',5),(2,'Purchase','two.mp3',5),
            (3,'Video','video.mp4',5),(4,'Remote','four.mp3',5),(5,'Cloud','five.mp3',5);
        ALTER TABLE item ADD COLUMN album_pid INTEGER DEFAULT 12;
        ALTER TABLE item ADD COLUMN album_artist_pid INTEGER DEFAULT 11;
        ALTER TABLE item ADD COLUMN item_artist_pid INTEGER DEFAULT 15;
        ALTER TABLE item ADD COLUMN composer_pid INTEGER DEFAULT 14;
        ALTER TABLE item ADD COLUMN genre_id INTEGER DEFAULT 16;
        ALTER TABLE item ADD COLUMN track_number INTEGER DEFAULT 1;
        ALTER TABLE item ADD COLUMN disc_number INTEGER DEFAULT 1;
        ALTER TABLE item ADD COLUMN is_compilation INTEGER DEFAULT 0;
        ALTER TABLE item_extra ADD COLUMN sort_title TEXT DEFAULT 'Title';
        ALTER TABLE item_extra ADD COLUMN comment TEXT;
        ALTER TABLE item_extra ADD COLUMN track_count INTEGER DEFAULT 1;
        ALTER TABLE item_extra ADD COLUMN disc_count INTEGER DEFAULT 1;
        ALTER TABLE item_extra ADD COLUMN year INTEGER DEFAULT 2026;
        ALTER TABLE item_extra ADD COLUMN total_time_ms REAL DEFAULT 1000;
        ALTER TABLE item_extra ADD COLUMN media_kind INTEGER DEFAULT 1;
        ALTER TABLE composer ADD COLUMN composer_pid INTEGER DEFAULT 14;
        ALTER TABLE item_artist ADD COLUMN item_artist_pid INTEGER DEFAULT 15;
        CREATE TABLE genre(genre_id INTEGER,genre TEXT);
        INSERT INTO genre VALUES(16,'Genre');
        CREATE TABLE item_playback(item_pid INTEGER,audio_format INTEGER,bit_rate INTEGER,sample_rate REAL,duration INTEGER);
        INSERT INTO item_playback SELECT item_pid,301,128,44100,44100 FROM item;
        CREATE TABLE item_stats(item_pid INTEGER,remember_bookmark INTEGER);
        INSERT INTO item_stats SELECT item_pid,0 FROM item;
        """;
    private readonly SyncWorkspace workspace = new();

    public SQLiteConnection Connection { get; }

    private string Path => System.IO.Path.Combine(this.workspace.Root, SyncPaths.DatabaseFile);

    public CatalogueFixture()
    {
        this.Connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = this.Path, Pooling = false }.ToString());
        this.Connection.Open();
        this.Execute(Schema);
    }

    public void Execute(string sql)
    {
        using var command = this.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void AddArtworkSchema()
    {
        this.Execute(ArtworkSchema);
    }

    public void ReplaceLocalTracks(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        this.Execute($"""
            DELETE FROM item;
            DELETE FROM item_store;
            DELETE FROM item_extra;
            WITH RECURSIVE tracks(id) AS (VALUES(1) UNION ALL SELECT id+1 FROM tracks WHERE id<{count})
            INSERT INTO item(item_pid,media_type,remote_location_id,base_location_id) SELECT id,8,0,1 FROM tracks;
            INSERT INTO item_store SELECT item_pid,100000+item_pid,0 FROM item;
            INSERT INTO item_extra(item_pid,title,location,file_size)
            SELECT item_pid,'Title','song-'||item_pid||'.mp3',5 FROM item;
            """);
    }

    public CatalogueSnapshot Snapshot()
    {
        return new CatalogueSnapshot(ReadShared(this.Path), File.Exists(this.Path + SyncPaths.WalSuffix)
            ? ReadShared(this.Path + SyncPaths.WalSuffix) : null);
    }

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public void Dispose()
    {
        this.Connection.Dispose();
        this.workspace.Dispose();
    }
}
