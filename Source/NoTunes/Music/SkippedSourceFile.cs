namespace NoTunes.Music;

public sealed record SkippedSourceFile(string RelativePath, string Reason, bool ArtworkOnly);
