using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class ReplyUsbMuxTransport(params Stream[] replies) : IUsbMuxTransport
{
    public int Connections { get; private set; }

    public Task<Stream> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(replies[this.Connections++]);
}
