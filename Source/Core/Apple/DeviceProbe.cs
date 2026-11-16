namespace NoTunes.Apple;

public static class DeviceProbe
{
    private const string IphoneModelPrefix = "iPhone";
    private const string FreeBytesKey = "FSFreeBytes";

    public static async Task<ProbeResult> RunAsync(UsbMuxClient mux, UsbDevice device,
        TimeSpan timeout, CancellationToken cancellationToken, bool readCatalogue = false)
    {
        await using var lockdown = await LockdownClient.ConnectAsync(mux, device, timeout, cancellationToken);
        await lockdown.StartPairedSessionAsync(cancellationToken);
        var info = await lockdown.GetInfoAsync(cancellationToken);
        if (!info.Model.StartsWith(IphoneModelPrefix, StringComparison.Ordinal))
            throw new IOException($"The selected device is {info.Model}, not an iPhone.");

        await using var afc = new AfcClient(await lockdown.OpenServiceAsync(DeviceServices.Afc, cancellationToken), timeout);
        var storage = await afc.GetDeviceInfoAsync(cancellationToken);
        var database = await afc.GetFileInfoAsync(CatalogueSnapshot.DatabasePath, cancellationToken);
        await using var atc = new AtcClient(await lockdown.OpenServiceAsync(DeviceServices.Atc, cancellationToken), timeout);
        var sync = await atc.ReadSyncStatusAsync(cancellationToken);
        var snapshot = readCatalogue ? await CatalogueSnapshot.CaptureAsync(afc, cancellationToken) : null;

        return new ProbeResult
        {
            Device = info,
            Paired = true,
            AfcAvailable = true,
            MusicDatabasePresent = database is not null,
            AtcAvailable = true,
            SyncAllowed = sync.SyncAllowed,
            DataProtected = sync.DataProtected,
            MusicTrackCount = sync.MusicTrackCount,
            MusicBytes = sync.MusicBytes,
            FreeBytes = storage.GetValueOrDefault(FreeBytesKey),
            CatalogueReadVerified = snapshot is not null,
            CatalogueBytes = snapshot?.Database.Length,
            CatalogueWalBytes = snapshot?.Wal?.Length
        };
    }
}
