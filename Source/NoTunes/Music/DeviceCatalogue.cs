namespace NoTunes.Music;

public sealed record DeviceCatalogue(
    long DatabaseId,
    long ContainerId,
    uint Anchor,
    IReadOnlyList<DeviceTrack> Tracks,
    IReadOnlyDictionary<string, long> Albums,
    IReadOnlyDictionary<string, long> Artists,
    IReadOnlySet<long> OccupiedIds)
{
    internal GroupSortNames SortNames { get; init; } = GroupSortNames.Empty;
}
