namespace NoTunes.Tests;

internal sealed class ScriptedStream(byte[] input, int maxReadBytes = 3) : NonSeekableTestStream
{
    private readonly MemoryStream incoming = new(input, writable: false);

    public MemoryStream Written { get; } = new();

    public bool WasDisposed { get; private set; }

    public override bool CanRead => !this.WasDisposed;

    public override bool CanWrite => !this.WasDisposed;









    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.incoming.Read(buffer, offset, Math.Min(count, maxReadBytes));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return this.incoming.ReadAsync(buffer[..Math.Min(buffer.Length, maxReadBytes)], cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) => this.Written.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return this.Written.WriteAsync(buffer, cancellationToken);
    }





    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.WasDisposed = true;
            this.incoming.Dispose();
            this.Written.Dispose();
        }

        base.Dispose(disposing);
    }
}
