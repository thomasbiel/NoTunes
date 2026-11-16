namespace NoTunes.Apple;

internal sealed class AfcFileLease(AfcClient client, ulong handle, CancellationToken cancellationToken) : IAsyncDisposable
{
    private int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0 || client.IsBroken)
            return;

        using var deadline = new CancellationTokenSource(AfcClient.CloseTimeout);
        try
        {
            await client.CloseFileAsync(handle, deadline.Token);
        }
        catch (OperationCanceledException e) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out releasing the iPhone sync lock.", e);
        }
    }
}
