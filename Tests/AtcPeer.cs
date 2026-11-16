using System.Buffers.Binary;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class AtcPeer : Stream
{
    private readonly MemoryStream incoming = new();
    private readonly MemoryStream written = new();
    private readonly List<long> frameEnds = [];

    public bool StallAtEnd { get; init; }

    public Action? BeforeRead { get; init; }

    public Func<int, CancellationToken, Task>? BeforeFrameAsync { get; init; }

    public Action<Dictionary<string, object>>? OnMessageSent { get; init; }

    public bool WasDisposed { get; private set; }

    public int ResponsesRead => this.frameEnds.Count(end => end <= this.incoming.Position);

    public AtcPeer(params Dictionary<string, object>[] messages)
    {
        foreach (var message in messages)
        {
            this.incoming.Write(ProtocolFrames.PlistMessage(message, atc: true));
            this.frameEnds.Add(this.incoming.Position);
        }

        this.incoming.Position = 0;
    }

    public List<Dictionary<string, object>> SentMessages()
    {
        var bytes = this.written.ToArray();
        var messages = new List<Dictionary<string, object>>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
            Assert.That(size, Is.InRange(1, bytes.Length - offset));
            messages.Add(BinaryPlist.Read([.. bytes.AsSpan(offset, size)]).AsDict());
            offset += size;
        }

        return messages;
    }

    public override bool CanRead => !this.WasDisposed;

    public override bool CanWrite => !this.WasDisposed;

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

    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.incoming.Read(buffer, offset, Math.Min(count, 3));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.BeforeRead?.Invoke();
        if (this.BeforeFrameAsync is not null
            && (this.incoming.Position == 0 || this.frameEnds.Contains(this.incoming.Position)))
        {
            await this.BeforeFrameAsync(this.ResponsesRead, cancellationToken);
        }

        if (this.StallAtEnd && this.incoming.Position == this.incoming.Length)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        return await this.incoming.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) => this.written.Write(buffer, offset, count);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await this.written.WriteAsync(buffer, cancellationToken);
        this.OnMessageSent?.Invoke(BinaryPlist.Read(buffer[sizeof(int)..].ToArray()).AsDict());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.WasDisposed = true;
            this.incoming.Dispose();
            this.written.Dispose();
        }

        base.Dispose(disposing);
    }
}
