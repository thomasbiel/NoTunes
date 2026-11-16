using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Scan, HelpText = "Recursively scan the source folder for MP3 files and compute SHA-256 hashes.")]
public sealed class ScanOptions
{
    [Value(1, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Value(0, MetaName = CliSyntax.Folder, HelpText = "Source folder; defaults to MusicSourceFolder in app.config.")]
    public string? Folder { get; set; }

    [Option(CliSyntax.Json, HelpText = "Print the file list including SHA-256 hashes as JSON.")]
    public bool Json { get; set; }
}
