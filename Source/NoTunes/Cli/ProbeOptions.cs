using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Probe, HelpText = "Check the iPhone, pairing and music sync service without modifying music.")]
public sealed class ProbeOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Option(CliSyntax.ReadCatalogue, HelpText = "Also verify two consistent read-only captures of the music database and its WAL.")]
    public bool ReadCatalogue { get; set; }
}
