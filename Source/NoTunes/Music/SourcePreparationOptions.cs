namespace NoTunes.Music;

internal sealed record SourcePreparationOptions
{
    public bool ArtworkOnly { get; init; }

    public GroupNamePrompt? Prompt { get; init; }

    public Action<SkippedSourceFile>? OnSkipped { get; init; }

    public TextWriter Error { get; init; } = TextWriter.Null;
}
