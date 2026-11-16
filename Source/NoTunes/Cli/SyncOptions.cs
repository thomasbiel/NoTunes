using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Sync, HelpText = "Mirror an MP3 folder to the Apple Music library (transfer is under development).")]
public sealed class SyncOptions : DeviceOptions
{
    [Value(1, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Value(0, MetaName = CliSyntax.Folder, HelpText = "Source folder; defaults to app.config. Subfolders are always included.")]
    public string? Folder { get; set; }

    [Option(CliSyntax.DryRun, HelpText = "Preview changes without transferring or deleting music.")]
    public bool DryRun { get; set; }
}
