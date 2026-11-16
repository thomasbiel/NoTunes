using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class AfcPeer : Stream
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public bool AllowWrites { get; init; }

    public bool FailWrite { get; init; }

    public bool CorruptWrite { get; init; }

    public List<byte[]> WritePackets { get; } = [];

    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

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

    public int OpenHandles => this.handles.Count;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => this.input.Read(buffer, offset, Math.Min(count, 5));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.closePending)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        return await this.input.ReadAsync(buffer[..Math.Min(buffer.Length, 5)], cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Handle(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => this.Handle([.. buffer.AsSpan(offset, count)]);

    private void Handle(byte[] request)
    {
        Assert.That(this.input.Position, Is.EqualTo(this.input.Length), "The previous response must be consumed first.");
        var operation = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(32));
        var id = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24));
        var body = request.AsSpan(40);
        switch (operation)
        {
            case 3:
                var directory = Encoding.UTF8.GetString(body).TrimEnd('\0');
                var names = this.Files.Keys.Where(name => name.StartsWith(directory + "/", StringComparison.Ordinal))
                    .Select(name => name[(directory.Length + 1)..]).ToArray();
                this.Response(id, 2, Encoding.UTF8.GetBytes(string.Join('\0', new[] { ".", ".." }.Concat(names)) + '\0'));
                return;
            case 9 when this.AllowWrites:
                this.Directories.Add(Encoding.UTF8.GetString(body).TrimEnd('\0'));
                this.Status(id, 0);
                return;
            case 10:
                var path = Encoding.UTF8.GetString(body).TrimEnd('\0');
                if (this.Directories.Contains(path))
                {
                    this.Response(id, 2, Encoding.UTF8.GetBytes("st_ifmt\0S_IFDIR\0"));
                    return;
                }

                if (!this.Files.TryGetValue(path, out var file))
                {
                    this.Status(id, 8);
                    return;
                }

                this.Response(id, 2, Encoding.UTF8.GetBytes($"st_ifmt\0S_IFREG\0st_size\0{file.Length}\0st_mtime\01\0"));
                return;
            case 13:
                var mode = BinaryPrimitives.ReadUInt64LittleEndian(body);
                Assert.That(mode == 1 || this.AllowWrites && mode is 2 or 3, Is.True, "Only explicitly enabled writes are allowed.");
                var name = Encoding.UTF8.GetString(body[8..]).TrimEnd('\0');
                if (mode == 3 || mode == 2 && !this.Files.ContainsKey(name))
                    this.Files[name] = [];
                Assert.That(this.Files.ContainsKey(name), Is.True);
                var handle = (ulong)++this.Opens;
                this.handles.Add(handle, (name, 0));
                var opened = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(opened, handle);
                this.Response(id, 14, opened);
                return;
            case 15:
                this.Reads++;
                var readHandle = BinaryPrimitives.ReadUInt64LittleEndian(body);
                var requested = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(body[8..]));
                var source = this.handles[readHandle];
                var count = this.EarlyEof ? 0 : Math.Min(Math.Min(this.ChunkLimit, requested), this.Files[source.Path].Length - source.Offset);
                this.Response(id, 2, [.. this.Files[source.Path].AsSpan(source.Offset, count)]);
                this.handles[readHandle] = (source.Path, source.Offset + count);
                return;
            case 16 when this.AllowWrites:
                this.WritePackets.Add(request);
                if (this.FailWrite)
                {
                    this.Status(id, 10);
                    return;
                }

                var target = this.handles[BinaryPrimitives.ReadUInt64LittleEndian(body)];
                var content = body[8..].ToArray();
                if (this.CorruptWrite && content.Length > 0)
                    content[0] ^= 1;
                this.Files[target.Path] = [.. this.Files[target.Path], .. content];
                this.Status(id, 0);
                return;
            case 24 when this.AllowWrites:
                var rename = Encoding.UTF8.GetString(body).TrimEnd('\0').Split('\0');
                Assert.That(this.Files.ContainsKey(rename[1]), Is.False);
                this.Files[rename[1]] = this.Files[rename[0]];
                this.Files.Remove(rename[0]);
                this.Status(id, 0);
                return;
            case 27 when this.AllowWrites:
                this.Status(id, 0);
                return;
            case 20:
                this.Closes++;
                Assert.That(this.handles.Remove(BinaryPrimitives.ReadUInt64LittleEndian(body)), Is.True);
                this.OnClose?.Invoke();
                if (this.StallClose)
                {
                    this.closePending = true;
                    return;
                }

                this.Status(id, this.FailClose ? 10UL : 0UL);
                return;
            default:
                Assert.Fail($"Unexpected AFC operation {operation}; the reader must not modify any files.");
                return;
        }
    }

    private void Status(ulong id, ulong code)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, code);
        this.Response(id, 1, body);
    }

    private void Response(ulong id, ulong operation, byte[] body)
    {
        this.input.Dispose();
        this.input = new MemoryStream(ProtocolFrames.Afc(id, operation, body), writable: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.WasDisposed = true;
            this.input.Dispose();
        }

        base.Dispose(disposing);
    }
}
