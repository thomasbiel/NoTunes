using System.Text.Json.Serialization;

namespace NoTunes.Music;

public sealed record SourceTrack(
    string RelativePath,
    long Size,
    DateTime LastWriteTimeUtc,
    [property: JsonPropertyName(ContentHash.AlgorithmName)] string Hash);
