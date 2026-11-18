using System.Buffers.Binary;
using System.Threading.Channels;
using NoTunes.Apple;

namespace NoTunes.Tests;

internal sealed class PlistPeer : NonSeekableTestStream
{
    private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
    private byte[] current = [];
    private int offset;

    public Action<Dictionary<string, object>>? OnMessage { get; init; }

    public bool WasDisposed { get; private set; }

    public override bool CanRead => !this.WasDisposed;

    public override bool CanWrite => !this.WasDisposed;

    public void Queue(Dictionary<string, object> message)
    {
        this.replies.Writer.TryWrite(ProtocolFrames.PlistMessage(message));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.offset == this.current.Length)
        {
            if (!await this.replies.Reader.WaitToReadAsync(cancellationToken)) return 0;

            this.current = await this.replies.Reader.ReadAsync(cancellationToken);
            this.offset = 0;
        }

        var count = Math.Min(buffer.Length, this.current.Length - this.offset);
        this.current.AsMemory(this.offset, count).CopyTo(buffer);
        this.offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = BinaryPrimitives.ReadInt32BigEndian(buffer.Span);
        if (length != buffer.Length - sizeof(int)) throw new InvalidDataException("Incomplete test plist frame.");

        this.OnMessage?.Invoke(Plist.Parse(buffer.Span[sizeof(int)..].ToArray()).AsDict());
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    }

    protected override void Dispose(bool disposing)
    {
        this.WasDisposed = true;
        this.replies.Writer.TryComplete();
        base.Dispose(disposing);
    }
}
