using CommandLine;
using NoTunes.Apple;

namespace NoTunes.Cli;

[Verb(CliSyntax.Probe, HelpText = "Check the iPhone, pairing and music sync service without modifying music.")]
public sealed class ProbeOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Option(CliSyntax.ReadCatalogue, HelpText = "Also verify two consistent read-only captures of the music database and its WAL.")]
    public bool ReadCatalogue { get; set; }

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        var mux = this.CreateMux(context);
        var device = SelectDevice(await mux.ListUsbDevicesAsync(token), this.Device);
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
            await context.Output.WriteLineAsync($"Device:          {result.Device.Name} ({result.Device.Model})");
            await context.Output.WriteLineAsync($"UDID:            {result.Device.Udid}");
            await context.Output.WriteLineAsync($"iOS:             {result.Device.IosVersion} ({result.Device.BuildVersion})");
            await context.Output.WriteLineAsync("USB pairing:     OK");
            await context.Output.WriteLineAsync("AFC file service: OK");
            await context.Output.WriteLineAsync($"Music database:  {(result.MusicDatabasePresent ? "present" : "not initialized")}");
            await context.Output.WriteLineAsync("ATC sync service: OK");
            await context.Output.WriteLineAsync($"Music sync:      {(result.SyncAllowed ? "allowed" : "blocked by iPhone")}");
            if (result.MusicTrackCount.HasValue) await context.Output.WriteLineAsync($"Music tracks:    {result.MusicTrackCount}");
            if (result.FreeBytes is not null) await context.Output.WriteLineAsync($"Free bytes:      {result.FreeBytes}");
            if (result.CatalogueReadVerified)
            {
                await context.Output.WriteLineAsync(
                    $"Catalogue read:  verified ({result.CatalogueBytes} database bytes, {result.CatalogueWalBytes ?? 0} WAL bytes)");
            }

            await context.Output.WriteLineAsync("No music was changed.");
        }

        return result.SyncAllowed ? ExitCodes.Success : ExitCodes.SyncBlocked;
    }
}
