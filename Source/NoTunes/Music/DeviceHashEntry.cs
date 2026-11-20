using System.Text.Json.Serialization;

namespace NoTunes.Music;

internal sealed record DeviceHashEntry(
    long Id,
    string Path,
    long Size,
    [property: JsonPropertyName(ContentHash.AlgorithmName)] string Hash,
    IReadOnlyDictionary<string, string> Attributes);
