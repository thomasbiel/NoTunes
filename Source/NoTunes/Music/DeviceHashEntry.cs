namespace NoTunes.Music;

internal sealed record DeviceHashEntry(
    long Id,
    string Path,
    long Size,
    string Sha256,
    IReadOnlyDictionary<string, string> Attributes);
