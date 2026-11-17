using NoTunes.Apple;

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
    internal IReadOnlyList<CatalogueGroup>? AlbumGroups { get; init; }

    internal IReadOnlyList<CatalogueGroup>? ArtistGroups { get; init; }

    internal GroupSortNames SortNames { get; init; } = GroupSortNames.Empty;

    internal IReadOnlyList<ProtectedCatalogueItem> ProtectedItems { get; init; } = [];

    internal bool ArtworkAvailable { get; init; }

    internal uint ArtworkHighWaterMark { get; init; } = ArtworkProtocol.CacheIdFloor;

    internal IReadOnlyDictionary<long, ArtworkReference> TrackArtwork { get; init; }
        = new Dictionary<long, ArtworkReference>();
}
