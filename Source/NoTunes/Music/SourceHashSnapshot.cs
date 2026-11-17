namespace NoTunes.Music;

internal sealed record SourceHashSnapshot(int Version, string Root, IReadOnlyList<SourceTrack> Tracks);
