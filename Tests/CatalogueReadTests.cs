using System.Buffers.Binary;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class CatalogueReadTests
{
    private const string DatabasePath = "iTunes_Control/iTunes/MediaLibrary.sqlitedb";

    [Test]
    public async Task ReadsPartialChunksAndExplicitlyClosesTheFile()
    {
        await using var peer = new AfcPeer { ChunkLimit = 2 };
        peer.Files["track"] = [1, 2, 3, 4, 5];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        Assert.That(await afc.ReadFileAsync("track", 5, CancellationToken.None), Is.EqualTo(peer.Files["track"]));
        Assert.That(peer.Reads, Is.EqualTo(3));
        Assert.That(peer.Closes, Is.EqualTo(1));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task RefusesToOpenAnOversizedFile()
    {
        await using var peer = new AfcPeer();
        peer.Files["track"] = new byte[6];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 5, CancellationToken.None),
            Throws.TypeOf<IOException>());
        Assert.That(peer.Opens, Is.Zero);
    }

    [Test]
    public async Task EarlyEofFailsAndStillClosesTheHandle()
    {
        await using var peer = new AfcPeer { EarlyEof = true };
        peer.Files["track"] = new byte[6];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 6, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(peer.Closes, Is.EqualTo(1));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task CloseFailurePreventsAReadFromBeingReportedAsComplete()
    {
        await using var peer = new AfcPeer { FailClose = true };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, CancellationToken.None),
            Throws.TypeOf<AfcException>());
    }

    [Test]
    public async Task CloseTimeoutIsAnIoErrorAndClosesTheConnection()
    {
        await using var peer = new AfcPeer { StallClose = true };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(30));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("closing"));
        Assert.That(peer.WasDisposed, Is.True);
        await Assert.ThatAsync(async () => await afc.GetFileInfoAsync("track", CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("closed"));
    }

    [Test]
    public async Task CallerCancellationDuringCloseIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        await using var peer = new AfcPeer { StallClose = true, OnClose = cancellation.Cancel };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(30));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(cancellation.IsCancellationRequested, Is.True);
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task CloseTimeoutDoesNotReplaceAnEarlierReadFailure()
    {
        await using var peer = new AfcPeer { EarlyEof = true, StallClose = true };
        peer.Files["track"] = [1];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(30));
        await Assert.ThatAsync(async () => await afc.ReadFileAsync("track", 1, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("read length"));
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task CapturesDatabaseAndWalTwice()
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
    public async Task RejectsByteDriftEvenWhenSizeAndAttributesStayTheSame()
    {
        await using var peer = new AfcPeer();
        peer.Files[DatabasePath] = Database();
        peer.OnClose = () => peer.Files[DatabasePath][100]++;
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("between verification reads"));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task RejectsAWalThatAppearsDuringCapture()
    {
        await using var peer = new AfcPeer();
        peer.Files[DatabasePath] = Database();
        peer.OnClose = () => peer.Files[DatabasePath + "-wal"] = [1, 2, 3];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("during capture"));
    }

    [Test]
    public async Task DoesNotAcceptAnUninitializedDatabase()
    {
        await using var afc = new AfcClient(new AfcPeer(), TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(async () => await CatalogueSnapshot.CaptureAsync(afc, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("not initialized"));
    }

    [Test]
    public void AttributeComparisonIgnoresOnlyAccessTime()
    {
        var first = new Dictionary<string, string> { ["st_size"] = "12", ["st_mtime"] = "1", ["st_atime"] = "1" };
        var second = new Dictionary<string, string>(first) { ["st_atime"] = "2" };
        Assert.That(AfcClient.SameAttributes(first, second), Is.True);
        second["st_mtime"] = "2";
        Assert.That(AfcClient.SameAttributes(first, second), Is.False);
        Assert.That(AfcClient.SameAttributes(first, null), Is.False);
    }

    private static byte[] Database()
    {
        var bytes = new byte[256];
        "SQLite format 3\0"u8.CopyTo(bytes);
        return bytes;
    }

    private sealed class AfcPeer : Stream
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, (string Path, int Offset)> handles = [];
        private MemoryStream input = new();
        private bool closePending;
        public int ChunkLimit { get; init; } = 1024 * 1024;
        public bool EarlyEof { get; init; }
        public bool FailClose { get; init; }
        public bool StallClose { get; init; }
        public bool WasDisposed { get; private set; }
        public Action? OnClose { get; set; }
        public int Opens { get; private set; }
        public int Closes { get; private set; }
        public int Reads { get; private set; }
        public int OpenHandles => handles.Count;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, Math.Min(count, 5));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (closePending) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await input.ReadAsync(buffer[..Math.Min(buffer.Length, 5)], cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Handle(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count) => Handle(buffer.AsSpan(offset, count).ToArray());

        private void Handle(byte[] request)
        {
            Assert.That(input.Position, Is.EqualTo(input.Length), "The previous response must be consumed first.");
            var operation = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(32));
            var id = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24));
            var body = request.AsSpan(40);
            switch (operation)
            {
                case 10:
                    var path = Encoding.UTF8.GetString(body).TrimEnd('\0');
                    if (!Files.TryGetValue(path, out var file)) { Status(id, 8); return; }
                    Response(id, 2, Encoding.UTF8.GetBytes($"st_ifmt\0S_IFREG\0st_size\0{file.Length}\0st_mtime\01\0"));
                    return;
                case 13:
                    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(body), Is.EqualTo(1), "Only AFC read-only opens are allowed.");
                    var name = Encoding.UTF8.GetString(body[8..]).TrimEnd('\0');
                    Assert.That(Files.ContainsKey(name), Is.True);
                    var handle = (ulong)++Opens;
                    handles.Add(handle, (name, 0));
                    var opened = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(opened, handle);
                    Response(id, 14, opened);
                    return;
                case 15:
                    Reads++;
                    var readHandle = BinaryPrimitives.ReadUInt64LittleEndian(body);
                    var requested = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(body[8..]));
                    var source = handles[readHandle];
                    var count = EarlyEof ? 0 : Math.Min(Math.Min(ChunkLimit, requested), Files[source.Path].Length - source.Offset);
                    Response(id, 2, Files[source.Path].AsSpan(source.Offset, count).ToArray());
                    handles[readHandle] = (source.Path, source.Offset + count);
                    return;
                case 20:
                    Closes++;
                    Assert.That(handles.Remove(BinaryPrimitives.ReadUInt64LittleEndian(body)), Is.True);
                    OnClose?.Invoke();
                    if (StallClose) { closePending = true; return; }
                    Status(id, FailClose ? 10UL : 0UL);
                    return;
                default: Assert.Fail($"Unexpected AFC operation {operation}; the reader must not modify any files."); return;
            }
        }

        private void Status(ulong id, ulong code)
        {
            var body = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(body, code);
            Response(id, 1, body);
        }

        private void Response(ulong id, ulong operation, byte[] body)
        {
            var packet = new byte[40 + body.Length];
            "CFA6LPAA"u8.CopyTo(packet);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), (ulong)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), id);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(32), operation);
            body.CopyTo(packet, 40);
            input.Dispose();
            input = new MemoryStream(packet, writable: false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { WasDisposed = true; input.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
