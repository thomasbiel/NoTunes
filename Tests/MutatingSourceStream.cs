namespace NoTunes.Tests;

internal sealed class MutatingSourceStream(string file, Action mutate) : FileStream(
    file,
    FileMode.Open,
    FileAccess.Read,
    FileShare.ReadWrite,
    bufferSize: 1,
    FileOptions.Asynchronous)
{
    public bool MutationPerformed { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await RandomAccess.ReadAsync(
            this.SafeFileHandle,
            buffer[..Math.Min(buffer.Length, 1)],
            this.Position,
            cancellationToken);

        this.Position += read;
        if (read > 0 && !this.MutationPerformed)
        {
            this.MutationPerformed = true;
            mutate();
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
