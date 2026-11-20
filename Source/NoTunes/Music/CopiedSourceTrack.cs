namespace NoTunes.Music;

internal sealed record CopiedSourceTrack(SourceTrack Source, string? LocalPath, SkippedSourceFile? Skipped = null);
