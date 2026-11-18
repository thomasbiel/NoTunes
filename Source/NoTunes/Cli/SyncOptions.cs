using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Sync, HelpText = "Mirror an MP3 folder to the Apple Music library.")]
public sealed class SyncOptions : DeviceOptions
{
    [Value(1, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Value(0, MetaName = CliSyntax.Folder, HelpText = "Source folder; defaults to app.config. Subfolders are always included.")]
    public string? Folder { get; set; }

    [Option(CliSyntax.DryRun, HelpText = "Preview changes without transferring or deleting music.")]
    public bool DryRun { get; set; }

    [Option(CliSyntax.Debug, HelpText = "Include per-track comparison and transfer diagnostics.")]
    public bool Debug { get; set; }

    [Option(CliSyntax.FullReadback, HelpText = "Read all comparison candidates from the iPhone and refresh cached hashes.")]
    public bool FullReadback { get; set; }

    [Option(CliSyntax.AllowEmptySource, HelpText = "Allow an empty source to remove all computer-synced music.")]
    public bool AllowEmptySource { get; set; }

    [Option(CliSyntax.StateFolder, HelpText = "Persistent sync journal folder; defaults to the user's local application data.")]
    public string? StateFolder { get; set; }

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        this.ValidateTimeout();
        var result = await context.SyncEngine.RunAsync(this, token);
        if (this.Json)
        {
            WriteJson(context.Output, result);
        }
        else
        {
            foreach (var track in result.Plan.Add)
            {
                await context.Output.WriteLineAsync($"+ {track.RelativePath}");
            }

            foreach (var track in result.Plan.Delete)
            {
                var state = track.IsIncomplete ? " [incomplete: no audio]" : string.Empty;
                await context.Output.WriteLineAsync($"- {track.Title} ({track.Id}){state}");
            }

            foreach (var track in result.Plan.Artwork)
            {
                await context.Output.WriteLineAsync($"~ Cover: {track.RelativePath}");
            }

            var covers = result.Plan.Artwork.Count == 0 ? string.Empty : $", {result.Plan.Artwork.Count} cover(s) repaired";
            var skipped = result.Plan.Skipped.Count == 0 ? string.Empty : $", {result.Plan.Skipped.Count} file(s) skipped";
            var deferred = result.Plan.DeferredDeletions.Count == 0
                ? string.Empty
                : $", {result.Plan.DeferredDeletions.Count} deletion(s) deferred";

            await context.Output.WriteLineAsync($"{(result.DryRun ? "Preview" : "Verified")}: "
                + $"{result.Plan.Add.Count} added, {result.Plan.Delete.Count} removed, "
                + $"{result.Plan.Unchanged} unchanged{covers}{skipped}{deferred}.");
        }

        return result.Plan.Skipped.Count == 0 ? ExitCodes.Success : ExitCodes.PartialSuccess;
    }
}
