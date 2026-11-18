using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record DeviceHashSnapshot(
    int Version,
    string Device,
    LibraryIdentity Identity,
    long DatabaseId,
    long ContainerId,
    IReadOnlyList<DeviceHashEntry> Tracks);
