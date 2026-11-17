using NoTunes.Apple;

namespace NoTunes.Tests;

internal sealed class SimulatedSyncConnection(AfcPeer files, AtcPeer messages, TimeSpan timeout) : IDeviceSyncConnection
{
    public AfcClient Afc { get; } = new(files, timeout);

    public AtcClient Atc { get; } = new(messages, timeout);

    public bool IsDisposed => files.WasDisposed && messages.WasDisposed;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.Atc.DisposeAsync();
        }
        finally
        {
            await this.Afc.DisposeAsync();
        }
    }
}
