using System.Text.Json;
using NoTunes.Apple;
using NoTunes.Music;
using NoTunes.Platform;

namespace NoTunes.Cli;

public sealed class Commands(SourceScanner sourceScanner, IUsbMuxTransport transport)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<int> DevicesAsync(DevicesOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        var mux = this.CreateMux(options);
        var devices = await mux.ListUsbDevicesAsync(cancellationToken);
        if (options.Device is not null)
            devices = [SelectDevice(devices, options.Device)];
        if (options.Json)
            WriteJson(devices);
        else if (devices.Count == 0)
            Console.WriteLine("No USB Apple devices found. Connect and unlock the iPhone.");
        else
            foreach (var device in devices)
                Console.WriteLine($"{device.Udid}  USB  product=0x{device.ProductId:X4}");
        return ExitCodes.Success;
    }

    public async Task<int> ProbeAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        var result = await this.GetProbeResultAsync(options, cancellationToken);
        if (options.Json)
            WriteJson(result);
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
            if (result.MusicTrackCount.HasValue)
                Console.WriteLine($"Music tracks:    {result.MusicTrackCount}");
            if (result.FreeBytes is not null)
                Console.WriteLine($"Free bytes:      {result.FreeBytes}");
            if (result.CatalogueReadVerified)
                Console.WriteLine($"Catalogue read:  verified ({result.CatalogueBytes} database bytes, {result.CatalogueWalBytes ?? 0} WAL bytes)");
            Console.WriteLine("No music was changed.");
        }

        return result.SyncAllowed ? ExitCodes.Success : ExitCodes.SyncBlocked;
    }

    public async Task<int> ScanAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        var library = await this.ScanSourceAsync(options.Folder, cancellationToken);
        if (options.Json)
            WriteJson(library);
        else
        {
            Console.WriteLine($"Source: {library.Root}");
            foreach (var track in library.Tracks)
                Console.WriteLine($"{track.Size,12}  {track.RelativePath}");
            Console.WriteLine($"{library.Tracks.Count} MP3 file(s), {library.TotalBytes} byte(s).");
        }

        return ExitCodes.Success;
    }

    public async Task<int> SyncAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        // Validate the source before even connecting to the phone.
        ValidateTimeout(options);
        _ = await this.ScanSourceAsync(options.Folder, cancellationToken);
        // Do not label a local file listing as a dry-run of changes to a phone we cannot inventory yet.
        Console.Error.WriteLine("Music synchronization is not implemented yet. " + "The native USB transport is available; signed ATC write sessions and verified library reconciliation are still missing. " + "No music was transferred or deleted. Use 'scan' and 'probe' to check the source and USB connection.");
        return ExitCodes.SyncNotImplemented;
    }

    public static UsbDevice SelectDevice(IReadOnlyList<UsbDevice> devices, string? udid)
    {
        if (udid is not null)
        {
            var normalized = DeviceUdid.Normalize(udid);
            return devices.SingleOrDefault(d => StringComparer.OrdinalIgnoreCase.Equals(DeviceUdid.Normalize(d.Udid), normalized)) ?? throw new IOException($"No USB device matches UDID '{udid}'.");
        }

        return devices.Count switch
        {
            0 => throw new IOException("No USB iPhone found. Connect it, unlock it and trust this computer."),
            1 => devices[0],
            _ => throw new IOException("Multiple USB Apple devices found. Select an iPhone with --device <UDID>.")
        };
    }

    private async Task<ProbeResult> GetProbeResultAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var mux = this.CreateMux(options);
        var device = SelectDevice(await mux.ListUsbDevicesAsync(cancellationToken), options.Device);
        return await DeviceProbe.RunAsync(
            mux,
            device,
            TimeSpan.FromSeconds(options.Timeout),
            cancellationToken,
            options.ReadCatalogue);
    }

    private UsbMuxClient CreateMux(DeviceOptions options)
    {
        ValidateTimeout(options);
        return new UsbMuxClient(transport, TimeSpan.FromSeconds(options.Timeout));
    }

    private static void ValidateTimeout(DeviceOptions options)
    {
        if (options.Timeout is < DeviceOptions.MinimumTimeoutSeconds or > DeviceOptions.MaximumTimeoutSeconds)
            throw new InvalidArgumentsException($"--{CliSyntax.Timeout} must be between {DeviceOptions.MinimumTimeoutSeconds} and {DeviceOptions.MaximumTimeoutSeconds} seconds.");
    }

    private Task<SourceLibrary> ScanSourceAsync(string? folder, CancellationToken cancellationToken)
    {
        return sourceScanner.ScanAsync(SourceSettings.ResolveFolder(folder), cancellationToken);
    }

    private static void ValidateAdditionalArguments(IEnumerable<string> arguments)
    {
        var additional = arguments.ToArray();
        if (additional.Length != 0)
            throw new InvalidArgumentsException($"Unexpected positional argument(s): {string.Join(", ", additional)}");
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
