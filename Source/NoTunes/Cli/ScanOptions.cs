using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Scan, HelpText = "Recursively scan the source folder for MP3 files and compute SHA-256 hashes.")]
public sealed class ScanOptions : CommandOptions
{
    [Value(1, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Value(0, MetaName = CliSyntax.Folder, HelpText = "Source folder; defaults to MusicSourceFolder in app.config.")]
    public string? Folder { get; set; }

    [Option(CliSyntax.Json, HelpText = "Print the file list including SHA-256 hashes as JSON.")]
    public bool Json { get; set; }

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        var library = await context.Scanner.ScanAsync(SourceSettings.ResolveFolder(this.Folder), token);
        if (this.Json)
        {
            WriteJson(context.Output, library);
        }
        else
        {
            await context.Output.WriteLineAsync($"Source: {library.Root}");
            foreach (var track in library.Tracks)
            {
                await context.Output.WriteLineAsync($"{DisplayFormat.Bytes(track.Size),12}  {track.RelativePath}");
            }

            await context.Output.WriteLineAsync(
                $"{DisplayFormat.Number(library.Tracks.Count)} MP3 file(s), {DisplayFormat.Bytes(library.TotalBytes)}.");
        }

        return ExitCodes.Success;
    }
}
