using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.RemoveCover, HelpText = "Remove all embedded covers from one local MP3 without changing its audio.")]
public sealed class RemoveCoverOptions : CommandOptions
{
    [Value(1, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    [Value(0, Required = true, MetaName = CliSyntax.File, HelpText = "MP3 path; relative paths use the configured music folder.")]
    public string File { get; set; } = string.Empty;

    [Option(CliSyntax.SourceFolder, HelpText = "Root for a relative MP3 path; defaults to MusicSourceFolder in app.config.")]
    public string? Folder { get; set; }

    [Option(CliSyntax.Json, HelpText = "Print the file path and removed-cover count as JSON.")]
    public bool Json { get; set; }

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);

        var path = Path.IsPathFullyQualified(this.File)
            ? this.File
            : Path.GetFullPath(this.File, Path.GetFullPath(SourceSettings.ResolveFolder(this.Folder)));

        var result = await context.CoverRemover.RemoveAsync(path, token);
        if (this.Json)
        {
            WriteJson(context.Output, result);
        }
        else
        {
            await context.Output.WriteLineAsync(result.RemovedCovers == 0
                ? $"No embedded covers found: {result.Path}"
                : $"Removed {DisplayFormat.Number(result.RemovedCovers)} embedded cover(s): {result.Path}");
        }

        return ExitCodes.Success;
    }
}
