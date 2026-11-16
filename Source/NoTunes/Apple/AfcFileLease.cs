namespace NoTunes.Apple;

internal sealed class AfcFileLease(AfcClient client, ulong handle) : IAsyncDisposable
{
    private int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0 || client.IsBroken)
            return;

        using var deadline = new CancellationTokenSource(AfcClient.CloseTimeout);
        await client.CloseFileAsync(handle, deadline.Token);
    }
}
