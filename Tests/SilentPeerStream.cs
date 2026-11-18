namespace NoTunes.Tests;

internal sealed class SilentPeerStream : NonSeekableTestStream
{
    public bool WasDisposed { get; private set; }

    public override bool CanRead => !this.WasDisposed;

    public override bool CanWrite => !this.WasDisposed;









    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }





    protected override void Dispose(bool disposing)
    {
        this.WasDisposed = true;
        base.Dispose(disposing);
    }
}
