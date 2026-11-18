using CommandLine;
using NoTunes.Apple;

namespace NoTunes.Cli;

[Verb(CliSyntax.Probe, HelpText = "Check the iPhone, pairing and music sync service without modifying music.")]
public sealed class ProbeOptions : DeviceOptions
{
    private const int LabelColumnWidth = 18;

    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Option(CliSyntax.ReadCatalogue, HelpText = "Also verify that the music library can be read consistently, including recent database changes.")]
    public bool ReadCatalogue { get; set; }

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        var mux = this.CreateMux(context);
        var device = await this.SelectDeviceAsync(mux, token);
        var result = await DeviceProbe.RunAsync(
            mux,
            device,
            TimeSpan.FromSeconds(this.Timeout),
            token,
            this.ReadCatalogue);

        if (this.Json)
        {
            WriteJson(context.Output, result);
        }
        else
        {
            await WriteTextAsync(context.Output, result, device.ConnectionType);
        }

        return result.SyncAllowed ? ExitCodes.Success : ExitCodes.SyncBlocked;
    }

    internal static async Task WriteTextAsync(TextWriter output, ProbeResult result, string connectionType)
    {
        await WriteRowAsync(output, "Device:", $"{result.Device.Name} ({result.Device.Model})");
        await WriteRowAsync(output, "Device ID:", result.Device.Udid);
        await WriteRowAsync(output, "iOS:", $"{result.Device.IosVersion} ({result.Device.BuildVersion})");
        await WriteRowAsync(output, "Connection:", connectionType == "Network" ? "Wi-Fi" : connectionType);
        await WriteRowAsync(output, "Music library:", result.MusicDatabasePresent ? "present" : "not initialized");
        await WriteRowAsync(output, "Music sync:", result.SyncAllowed ? "allowed" : "blocked by iPhone");
        if (result.MusicTrackCount.HasValue) await WriteRowAsync(output, "Music tracks:", result.MusicTrackCount.Value);
        if (result.FreeBytes is not null) await WriteRowAsync(output, "Free bytes:", result.FreeBytes);

        if (result.CatalogueReadVerified)
        {
            await WriteRowAsync(output, "Database size:", $"{result.CatalogueBytes:G0} bytes");
        }
    }

    private static Task WriteRowAsync(TextWriter output, string label, object value)
    {
        return output.WriteLineAsync($"{label,-LabelColumnWidth}{value}");
    }
}
