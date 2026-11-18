using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Init, HelpText = "Initialize an iPhone that has never been synced with a music library.")]
public sealed class InitOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Option(CliSyntax.DryRun, HelpText = "Inspect initialization without changing the iPhone.")]
    public bool DryRun { get; set; }

    [Option(CliSyntax.StateFolder, HelpText = "Persistent initialization and recovery state folder.")]
    public string? StateFolder { get; set; }

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        this.ValidateDeviceOptions();
        var result = await context.Initializer.RunAsync(this, token);
        if (this.Json)
        {
            WriteJson(context.Output, result);
        }
        else
        {
            await context.Output.WriteLineAsync(result.DryRun ? "Initialization preview; no device files changed."
                : result.AlreadyInitialized ? "This NoTunes library is already initialized." : "Music library initialized.");
        }

        return ExitCodes.Success;
    }
}
