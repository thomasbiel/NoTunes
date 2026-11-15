using CommandLine;

namespace NoTunes.Cli;

public abstract class DeviceOptions
{
    [Option("device", MetaValue = "UDID", HelpText = "Select a USB iPhone by its UDID.")]
    public string? Device { get; set; }

    [Option("timeout", Default = 15, MetaValue = "SECONDS", HelpText = "Timeout per device request (1–300 seconds).")]
    public int Timeout { get; set; }

    [Option("json", HelpText = "Print the result as JSON.")]
    public bool Json { get; set; }
}

[Verb("devices", HelpText = "List Apple devices connected over USB.")]
public sealed class DevicesOptions : DeviceOptions;

[Verb("probe", HelpText = "Check the iPhone, pairing and music sync service without modifying music.")]
public sealed class ProbeOptions : DeviceOptions
{
    [Option("read-catalog", HelpText = "Also verify two consistent read-only captures of the music database and its WAL.")]
    public bool ReadCatalogue { get; set; }
}

[Verb("scan", HelpText = "Recursively scan the source folder for MP3 files and compute SHA-256 hashes.")]
public sealed class ScanOptions
{
    [Value(0, MetaName = "FOLDER", HelpText = "Source folder; defaults to MusicSourceFolder in app.config.")]
    public string? Folder { get; set; }

    [Option("json", HelpText = "Print the file list including SHA-256 hashes as JSON.")]
    public bool Json { get; set; }
}

[Verb("sync", HelpText = "Mirror an MP3 folder to the Apple Music library (transfer is under development).")]
public sealed class SyncOptions : DeviceOptions
{
    [Value(0, MetaName = "FOLDER", HelpText = "Source folder; defaults to app.config. Subfolders are always included.")]
    public string? Folder { get; set; }

    [Option("dry-run", HelpText = "Preview changes without transferring or deleting music.")]
    public bool DryRun { get; set; }
}
