namespace NoTunes.Apple;

public sealed record ProbeResult(DeviceInfo Device, bool Paired, bool AfcAvailable,
    bool MusicDatabasePresent, bool AtcAvailable, bool SyncAllowed, bool DataProtected,
    long? MusicTrackCount, long? MusicBytes, string? FreeBytes,
    bool CatalogueReadVerified, int? CatalogueBytes, int? CatalogueWalBytes);

public static class DeviceProbe
{
    public static async Task<ProbeResult> RunAsync(UsbMuxClient mux, UsbDevice device,
        TimeSpan timeout, CancellationToken cancellationToken, bool readCatalogue = false)
    {
        await using var lockdown = await LockdownClient.ConnectAsync(mux, device, timeout, cancellationToken);
        await lockdown.StartPairedSessionAsync(cancellationToken);
        var info = await lockdown.GetInfoAsync(cancellationToken);
        if (!info.Model.StartsWith("iPhone", StringComparison.Ordinal))
            throw new IOException($"The selected device is {info.Model}, not an iPhone.");
        await using var afc = new AfcClient(await lockdown.OpenServiceAsync("com.apple.afc", cancellationToken), timeout);
        var storage = await afc.GetDeviceInfoAsync(cancellationToken);
        var database = await afc.GetFileInfoAsync("iTunes_Control/iTunes/MediaLibrary.sqlitedb", cancellationToken);
        await using var atc = new PlistConnection(await lockdown.OpenServiceAsync("com.apple.atc", cancellationToken), timeout, atc: true);
        await atc.SendAsync(Plist.Dict(("Command", "Ping"), ("Session", 1L)), cancellationToken);
        long? count = null;
        long? bytes = null;
        // Bound unsolicited startup frames rather than waiting indefinitely for a particular message.
        for (var i = 0; i < 24; i++)
        {
            var message = await atc.ReceiveAsync(cancellationToken);
            var command = message.Text("Command");
            if (command == "Ping")
            {
                await atc.SendAsync(Plist.Dict(("Command", "Pong"), ("Session", message.Number("Session"))), cancellationToken);
                continue;
            }
            if (command is "SyncFailed" or "ConnectionInvalid")
                throw new IOException($"The iPhone music sync service returned {command}.");
            if (command == "AssetMetrics" && message.TryGetValue("Params", out var metrics)
                && metrics.AsDict().TryGetValue("Media", out var media)
                && media.AsDict().TryGetValue("Music", out var music))
            {
                if (music.AsDict().TryGetValue("_Count", out var number) && number is long value) count = value;
                if (music.AsDict().TryGetValue("_PhysicalSize", out var size) && size is long length) bytes = length;
            }
            if (command != "SyncAllowed") continue;
            var parameters = message.TryGetValue("Params", out var data) ? data.AsDict()
                : throw new InvalidDataException("Music sync permissions are missing.");
            var protectedData = parameters.Flag("DataProtected");
            var musicDisabled = parameters.TryGetValue("DisabledAssetTypes", out var disabled)
                && disabled is object[] types && types.Contains("Music");
            var allowed = (parameters.Flag("ManualSync") || parameters.Flag("AutoSync")) && !protectedData && !musicDisabled;
            var snapshot = readCatalogue ? await CatalogueSnapshot.CaptureAsync(afc, cancellationToken) : null;
            return new ProbeResult(info, true, true, database is not null, true, allowed,
                protectedData, count, bytes, storage.GetValueOrDefault("FSFreeBytes"), snapshot is not null,
                snapshot?.Database.Length, snapshot?.Wal?.Length);
        }
        throw new IOException("The iPhone did not report its music sync permissions within the startup message limit.");
    }
}
