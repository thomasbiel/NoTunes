using System.Diagnostics.CodeAnalysis;

namespace NoTunes.Music;

internal sealed record SourcePreparationOptions
{
    public bool ArtworkOnly { get; init; }

    public GroupNamePrompt? Prompt { get; init; }

    public Action<SkippedSourceFile>? OnSkipped { get; init; }

    public Func<string, string, ArtworkCache?, Mp3Metadata> ReadMetadata { get; init; } = Mp3Metadata.Read;

    public TextWriter Error { get; init; } = TextWriter.Null;

    public bool TryDescribeSkippedFile(
        Exception exception,
        SourceTrack track,
        [NotNullWhen(true)] out SkippedSourceFile? skipped)
    {
        skipped = null;
        var isMetadataError = exception
            is InvalidDataException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException;

        if (this.OnSkipped is null || !isMetadataError)
        {
            return false;
        }

        skipped = new SkippedSourceFile(track.RelativePath, exception.GetBaseException().Message, this.ArtworkOnly);
        return true;
    }
}
