using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class AfcPeer : Stream
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

    public override void Write(byte[] buffer, int offset, int count) => this.Handle(buffer.AsSpan(offset, count).ToArray());

    private void Handle(byte[] request)
    {
        Assert.That(this.input.Position, Is.EqualTo(this.input.Length), "The previous response must be consumed first.");
        var operation = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(32));
        var id = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24));
        var body = request.AsSpan(40);
        switch (operation)
        {
            case 10:
                var path = Encoding.UTF8.GetString(body).TrimEnd('\0');
                if (!this.Files.TryGetValue(path, out var file))
                {
                    this.Status(id, 8);
                    return;
                }

                this.Response(id, 2, Encoding.UTF8.GetBytes($"st_ifmt\0S_IFREG\0st_size\0{file.Length}\0st_mtime\01\0"));
                return;
            case 13:
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(body), Is.EqualTo(1), "Only AFC read-only opens are allowed.");
                var name = Encoding.UTF8.GetString(body[8..]).TrimEnd('\0');
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
                this.Response(id, 2, this.Files[source.Path].AsSpan(source.Offset, count).ToArray());
                this.handles[readHandle] = (source.Path, source.Offset + count);
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
