using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CatalogueReadTests
{
    private const string DatabasePath = "iTunes_Control/iTunes/MediaLibrary.sqlitedb";

    [Test]
    public async Task Reads_An_Empty_File_Without_Requesting_Data()
    {
        await using var peer = new AfcPeer();
        peer.Files["empty"] = [];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        Assert.That(await afc.ReadFileAsync("empty", 0, CancellationToken.None), Is.Empty);
        Assert.That(peer.Reads, Is.Zero);
        Assert.That(peer.Closes, Is.EqualTo(1));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Rejects_A_Size_Change_After_Reading_The_Declared_Bytes([Values] bool buffered)
    {
        await using var peer = new AfcPeer();
        peer.Files["track"] = [1, 2, 3];
        peer.OnClose = () => peer.Files["track"] = [1, 2, 3, 4];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (buffered)
            {
                await afc.ReadFileAsync("track", 3, CancellationToken.None);
            }
            else
            {
                await afc.CopyFileToAsync(
                    "track",
                    output,
                    3,
                    CancellationToken.None);
            }
        });
        Assert.That(peer.Closes, Is.EqualTo(1));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Reads_Partial_Chunks_And_Explicitly_Closes_The_File()
    {
        await using var peer = new AfcPeer
        {
            ChunkLimit = 2
        };
        peer.Files["track"] = [1, 2, 3, 4, 5];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        Assert.That(await afc.ReadFileAsync("track", 5, CancellationToken.None), Is.EqualTo(peer.Files["track"]));
        Assert.That(peer.Reads, Is.EqualTo(3));
        Assert.That(peer.Closes, Is.EqualTo(1));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Refuses_To_Open_An_Oversized_File()
    {
        await using var peer = new AfcPeer();
        peer.Files["track"] = new byte[6];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 5, CancellationToken.None), Throws.TypeOf<IOException>());
        Assert.That(peer.Opens, Is.Zero);
    }

    [Test]
    public async Task Early_Eof_Fails_And_Still_Closes_The_Handle()
    {
        await using var peer = new AfcPeer
        {
            EarlyEof = true
        };
        peer.Files["track"] = new byte[6];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 6, CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        Assert.That(peer.Closes, Is.EqualTo(1));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Close_Failure_Prevents_A_Read_From_Being_Reported_As_Complete()
    {
        await using var peer = new AfcPeer
        {
            FailClose = true
        };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, CancellationToken.None), Throws.TypeOf<AfcException>());
    }

    [Test]
    public async Task Close_Timeout_Is_An_Io_Error_And_Closes_The_Connection()
    {
        await using var peer = new AfcPeer
        {
            StallClose = true
        };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(30));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("closing"));
        Assert.That(peer.WasDisposed, Is.True);
        await Assert.ThatAsync(async () => await afc.GetFileInfoAsync("track", CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("closed"));
    }

    [Test]
    public async Task Caller_Cancellation_During_Close_Is_Preserved()
    {
        using var cancellation = new CancellationTokenSource();
        await using var peer = new AfcPeer
        {
            StallClose = true,
            OnClose = cancellation.Cancel
        };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(30));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(cancellation.IsCancellationRequested, Is.True);
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Close_Timeout_Does_Not_Replace_An_Earlier_Read_Failure()
    {
        await using var peer = new AfcPeer
        {
            EarlyEof = true,
            StallClose = true
        };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(30));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, CancellationToken.None), Throws.TypeOf<InvalidDataException>().With.Message.Contains("read length"));
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Captures_Database_And_Wal_Twice()
    {
        await using var peer = new AfcPeer();
        peer.Files[DatabasePath] = Database();
        peer.Files[DatabasePath + "-wal"] = [1, 2, 3];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        var snapshot = await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None);
        Assert.That(snapshot.Database, Is.EqualTo(peer.Files[DatabasePath]));
        Assert.That(snapshot.Wal, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(peer.Opens, Is.EqualTo(4));
        Assert.That(peer.Closes, Is.EqualTo(4));
    }

    [Test]
    public async Task Rejects_Byte_Drift_Even_When_Size_And_Attributes_Stay_The_Same()
    {
        await using var peer = new AfcPeer();
        peer.Files[DatabasePath] = Database();
        peer.OnClose = () => peer.Files[DatabasePath][100]++;
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("between verification reads"));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Rejects_A_Wal_That_Appears_During_Capture()
    {
        await using var peer = new AfcPeer();
        peer.Files[DatabasePath] = Database();
        peer.OnClose = () => peer.Files[DatabasePath + "-wal"] = [1, 2, 3];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("during capture"));
    }

    [Test]
    public async Task Does_Not_Accept_An_Uninitialized_Database()
    {
        await using var afc = new AfcClient(new AfcPeer(), TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("not initialized"));
    }

    [Test]
    public void Attribute_Comparison_Ignores_Only_Access_Time()
    {
        var first = new Dictionary<string, string>
        {
            ["st_size"] = "12",
            ["st_mtime"] = "1",
            ["st_atime"] = "1"
        };
        var second = new Dictionary<string, string>(first)
        {
            ["st_atime"] = "2"
        };
        Assert.That(AfcClient.SameAttributes(first, second), Is.True);
        second["st_mtime"] = "2";
        Assert.That(AfcClient.SameAttributes(first, second), Is.False);
        Assert.That(AfcClient.SameAttributes(first, null), Is.False);
    }

    [TestCase(AfcKeys.FileSize, "5", true)]
    [TestCase("ST_SIZE", "5", false)]
    [TestCase(AfcKeys.FileSize, "05", false)]
    public void Compares_Stable_Attribute_Names_And_Values_Ordinally(
        string sizeKey,
        string size,
        bool expected)
    {
        const string regularFile = "S_IFREG";
        var left = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AfcKeys.FileType] = regularFile,
            [AfcKeys.FileSize] = "5",
            [AfcKeys.AccessTime] = "1"
        };
        var right = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [sizeKey] = size,
            [AfcKeys.FileType] = regularFile
        };

        Assert.That(AfcClient.SameAttributes(left, right), Is.EqualTo(expected));
        Assert.That(AfcClient.SameAttributes(right, left), Is.EqualTo(expected));
    }

    private static byte[] Database()
    {
        var bytes = new byte[256];
        "SQLite format 3\0"u8.CopyTo(bytes);
        return bytes;
    }
}
