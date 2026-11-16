namespace NoTunes.Apple;

internal sealed class DeviceSyncConnection(LockdownClient lockdown, AfcClient afc, AtcClient atc, DeviceInfo device) : IAsyncDisposable
{
    public AfcClient Afc { get; } = afc;

    public AtcClient Atc { get; } = atc;

    public DeviceInfo Device { get; } = device;

    public static async Task<DeviceSyncConnection> OpenAsync(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lockdown = await LockdownClient.ConnectAsync(
            mux,
            device,
            timeout,
            cancellationToken);

        AfcClient? afc = null;
        AtcClient? atc = null;
        try
        {
            await lockdown.StartPairedSessionAsync(cancellationToken);
            var info = await lockdown.GetInfoAsync(cancellationToken);
            if (!info.Model.StartsWith("iPhone", StringComparison.Ordinal))
            {
                throw new IOException("Music synchronization requires an iPhone.");
            }

            afc = new AfcClient(await lockdown.OpenServiceAsync(DeviceServices.Afc, cancellationToken), timeout);
            atc = new AtcClient(await lockdown.OpenServiceAsync(DeviceServices.Atc, cancellationToken), timeout);
            if (!(await atc.ReadSyncStatusAsync(cancellationToken)).SyncAllowed)
            {
                throw new IOException("The iPhone does not permit music synchronization.");
            }

            return new DeviceSyncConnection(
                lockdown,
                afc,
                atc,
                info);
        }
        catch
        {
            if (atc is not null) await atc.DisposeAsync();
            if (afc is not null) await afc.DisposeAsync();
            await lockdown.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.Atc.DisposeAsync();
        }
        finally
        {
            try
            {
                await this.Afc.DisposeAsync();
            }
            finally
            {
                await lockdown.DisposeAsync();
            }
        }
    }
}
