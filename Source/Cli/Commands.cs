using System.Text.Json;
using NoTunes.Apple;
using NoTunes.Music;

namespace NoTunes.Cli;

public static class Commands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> DevicesAsync(DevicesOptions options, CancellationToken cancellationToken)
    {
        var mux = CreateMux(options);
        var devices = await mux.ListUsbDevicesAsync(cancellationToken);
        if (options.Device is not null) devices = [SelectDevice(devices, options.Device)];
        if (options.Json) WriteJson(devices);
        else if (devices.Count == 0) Console.WriteLine("No USB Apple devices found. Connect and unlock the iPhone.");
        else foreach (var device in devices) Console.WriteLine($"{device.Udid}  USB  product=0x{device.ProductId:X4}");
        return 0;
    }

    public static async Task<int> ProbeAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var result = await GetProbeResultAsync(options, cancellationToken);
        if (options.Json) WriteJson(result);
        else
        {
            Console.WriteLine($"Device:          {result.Device.Name} ({result.Device.Model})");
            Console.WriteLine($"UDID:            {result.Device.Udid}");
            Console.WriteLine($"iOS:             {result.Device.IosVersion} ({result.Device.BuildVersion})");
            Console.WriteLine("USB pairing:     OK");
            Console.WriteLine("AFC file service: OK");
            Console.WriteLine($"Music database:  {(result.MusicDatabasePresent ? "present" : "not initialized")}");
            Console.WriteLine("ATC sync service: OK");
            Console.WriteLine($"Music sync:      {(result.SyncAllowed ? "allowed" : "blocked by iPhone")}");
            if (result.MusicTrackCount.HasValue) Console.WriteLine($"Music tracks:    {result.MusicTrackCount}");
            if (result.FreeBytes is not null) Console.WriteLine($"Free bytes:      {result.FreeBytes}");
            if (result.CatalogueReadVerified)
                Console.WriteLine($"Catalogue read:  verified ({result.CatalogueBytes} database bytes, {result.CatalogueWalBytes ?? 0} WAL bytes)");
            Console.WriteLine("No music was changed.");
        }
        return result.SyncAllowed ? 0 : 3;
    }

    public static async Task<int> ScanAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        var library = await SourceScanner.ScanAsync(SourceSettings.ResolveFolder(options.Folder), cancellationToken);
        if (options.Json) WriteJson(library);
        else
        {
            Console.WriteLine($"Source: {library.Root}");
            foreach (var track in library.Tracks) Console.WriteLine($"{track.Size,12}  {track.RelativePath}");
            Console.WriteLine($"{library.Tracks.Count} MP3 file(s), {library.TotalBytes} byte(s).");
        }
        return 0;
    }

    public static async Task<int> SyncAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        // Validate the source before even connecting to the phone.
        ValidateTimeout(options);
        _ = await SourceScanner.ScanAsync(SourceSettings.ResolveFolder(options.Folder), cancellationToken);
        // Do not label a local file listing as a dry-run of changes to a phone we cannot inventory yet.
        Console.Error.WriteLine("Music synchronization is not implemented yet. " +
            "The native USB transport is available; signed ATC write sessions and verified library reconciliation are still missing. " +
            "No music was transferred or deleted. Use 'scan' and 'probe' to check the source and USB connection.");
        return 4;
    }

    public static UsbDevice SelectDevice(IReadOnlyList<UsbDevice> devices, string? udid)
    {
        if (udid is not null)
        {
            var normalized = udid.Replace("-", "", StringComparison.Ordinal);
            return devices.SingleOrDefault(d => d.Udid.Replace("-", "", StringComparison.Ordinal)
                .Equals(normalized, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException($"No USB device matches UDID '{udid}'.");
        }
        return devices.Count switch
        {
            0 => throw new IOException("No USB iPhone found. Connect it, unlock it and trust this computer."),
            1 => devices[0],
            _ => throw new IOException("Multiple USB Apple devices found. Select an iPhone with --device <UDID>.")
        };
    }

    private static async Task<ProbeResult> GetProbeResultAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var mux = CreateMux(options);
        var device = SelectDevice(await mux.ListUsbDevicesAsync(cancellationToken), options.Device);
        return await DeviceProbe.RunAsync(mux, device, TimeSpan.FromSeconds(options.Timeout), cancellationToken,
            options.ReadCatalogue);
    }

    private static UsbMuxClient CreateMux(DeviceOptions options)
    {
        ValidateTimeout(options);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native USB support currently requires Windows.");
        return new UsbMuxClient(TimeSpan.FromSeconds(options.Timeout));
    }

    private static void ValidateTimeout(DeviceOptions options)
    {
        if (options.Timeout is < 1 or > 300) throw new ArgumentException("--timeout must be between 1 and 300 seconds.");
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
