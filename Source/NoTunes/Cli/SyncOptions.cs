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

    [Option(CliSyntax.AllowEmptySource, HelpText = "Allow an empty source to remove all computer-synced music.")]
    public bool AllowEmptySource { get; set; }

    [Option(CliSyntax.StateFolder, HelpText = "Persistent sync journal folder; defaults to the user's local application data.")]
    public string? StateFolder { get; set; }
}
