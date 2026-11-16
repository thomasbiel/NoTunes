using System.Data.SQLite;
using NoTunes.Apple;
using NoTunes.Music;

namespace NoTunes.Tests;

internal sealed class CatalogueFixture : IDisposable
{
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
        CREATE TABLE album_artist(album_artist_pid INTEGER,sync_id INTEGER,album_artist TEXT);
        INSERT INTO album_artist VALUES(11,1011,'Artist');
        CREATE TABLE album(album_pid INTEGER,sync_id INTEGER,album TEXT,album_artist_pid INTEGER);
        INSERT INTO album VALUES(12,1012,'Album',11);
        CREATE TABLE container(sync_id INTEGER);
        INSERT INTO container VALUES(1002);
        CREATE TABLE composer(sync_id INTEGER);
        INSERT INTO item VALUES(1,8,0,1),(2,8,0,1),(3,2,0,1),(4,8,1,1),(5,8,0,1);
        INSERT INTO item_store VALUES(1,101,0),(2,0,999),(3,103,0),(4,104,0),(5,105,555);
        INSERT INTO item_extra VALUES(1,'Title','one.mp3',5),(2,'Purchase','two.mp3',5),
            (3,'Video','video.mp4',5),(4,'Remote','four.mp3',5),(5,'Cloud','five.mp3',5);
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
