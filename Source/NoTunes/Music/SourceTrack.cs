namespace NoTunes.Music;

public sealed record SourceTrack(string RelativePath, long Size, DateTime LastWriteTimeUtc, string Sha256);
