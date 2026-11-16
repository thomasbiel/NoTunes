using CommandLine;

namespace NoTunes.Cli;

public abstract class DeviceOptions
{
    internal const int MinimumTimeoutSeconds = 1;
    internal const int MaximumTimeoutSeconds = 300;
    internal const int DefaultTimeoutSeconds = 15;

    [Option(CliSyntax.Device, MetaValue = CliSyntax.Udid, HelpText = "Select a USB iPhone by its UDID.")]
    public string? Device { get; set; }

    [Option(CliSyntax.Timeout, Default = DefaultTimeoutSeconds, MetaValue = CliSyntax.Seconds, HelpText = "Timeout per device request (1–300 seconds).")]
    public int Timeout { get; set; }

    [Option(CliSyntax.Json, HelpText = "Print the result as JSON.")]
    public bool Json { get; set; }
}
