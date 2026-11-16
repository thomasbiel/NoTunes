using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Devices, HelpText = "List Apple devices connected over USB.")]
public sealed class DevicesOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];
}
