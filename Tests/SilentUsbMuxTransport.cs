using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class SilentUsbMuxTransport : IUsbMuxTransport
{
    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new NotSupportedException();
    }
}
