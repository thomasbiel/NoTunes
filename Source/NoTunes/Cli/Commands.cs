using System.Text.Json;
using NoTunes.Apple;
using NoTunes.Music;
using NoTunes.Platform;

namespace NoTunes.Cli;

public sealed class Commands(SourceScanner sourceScanner, IUsbMuxTransport transport, SyncEngine syncEngine, TextWriter output)
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
        if (options.Device is not null) devices = [SelectDevice(devices, options.Device)];

        if (options.Json)
        {
            this.WriteJson(devices);
        }

        else if (devices.Count == 0)
        {
            await output.WriteLineAsync("No USB Apple devices found. Connect and unlock the iPhone.");
        }

        else
        {
            foreach (var device in devices)
            {
                await output.WriteLineAsync($"{device.Udid}  USB  product=0x{device.ProductId:X4}");
            }
        }

        return ExitCodes.Success;
    }

    public async Task<int> ProbeAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        var result = await this.GetProbeResultAsync(options, cancellationToken);
        if (options.Json)
            this.WriteJson(result);
        else
        {
            await output.WriteLineAsync($"Device:          {result.Device.Name} ({result.Device.Model})");
            await output.WriteLineAsync($"UDID:            {result.Device.Udid}");
            await output.WriteLineAsync($"iOS:             {result.Device.IosVersion} ({result.Device.BuildVersion})");
            await output.WriteLineAsync("USB pairing:     OK");
            await output.WriteLineAsync("AFC file service: OK");
            await output.WriteLineAsync($"Music database:  {(result.MusicDatabasePresent ? "present" : "not initialized")}");
            await output.WriteLineAsync("ATC sync service: OK");
            await output.WriteLineAsync($"Music sync:      {(result.SyncAllowed ? "allowed" : "blocked by iPhone")}");
            if (result.MusicTrackCount.HasValue)
                await output.WriteLineAsync($"Music tracks:    {result.MusicTrackCount}");
            if (result.FreeBytes is not null)
                await output.WriteLineAsync($"Free bytes:      {result.FreeBytes}");
            if (result.CatalogueReadVerified)
                await output.WriteLineAsync($"Catalogue read:  verified ({result.CatalogueBytes} database bytes, {result.CatalogueWalBytes ?? 0} WAL bytes)");
            await output.WriteLineAsync("No music was changed.");
        }

        return result.SyncAllowed ? ExitCodes.Success : ExitCodes.SyncBlocked;
    }

    public async Task<int> ScanAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        var library = await this.ScanSourceAsync(options.Folder, cancellationToken);
        if (options.Json)
        {
            this.WriteJson(library);
        }

        else
        {
            await output.WriteLineAsync($"Source: {library.Root}");
            foreach (var track in library.Tracks)
            {
                await output.WriteLineAsync($"{track.Size,12}  {track.RelativePath}");
            }

            await output.WriteLineAsync($"{library.Tracks.Count} MP3 file(s), {library.TotalBytes} byte(s).");
        }

        return ExitCodes.Success;
    }

    public async Task<int> SyncAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        ValidateAdditionalArguments(options.AdditionalArguments);
        ValidateTimeout(options);
        var result = await syncEngine.RunAsync(options, cancellationToken);
        if (options.Json)
        {
            this.WriteJson(result);
        }

        else
        {
            foreach (var track in result.Plan.Add)
            {
                await output.WriteLineAsync($"+ {track.RelativePath}");
            }

            foreach (var track in result.Plan.Delete)
            {
                await output.WriteLineAsync($"- {track.Title} ({track.Id})");
            }

            await output.WriteLineAsync($"{(result.DryRun ? "Preview" : "Verified")}: {result.Plan.Add.Count} added, {result.Plan.Delete.Count} removed, {result.Plan.Unchanged} unchanged.");
        }

        return ExitCodes.Success;
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

    private void WriteJson<T>(T value) => output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
