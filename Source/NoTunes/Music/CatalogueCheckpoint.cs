namespace NoTunes.Music;

internal sealed record CatalogueCheckpoint(
    long DatabaseId,
    long ContainerId,
    uint Anchor,
    DeviceTrack[] Tracks,
    Dictionary<string, long> Albums,
    Dictionary<string, long> Artists,
    long[] OccupiedIds,
    GroupSortNames SortNames,
    ProtectedCatalogueItem[] ProtectedItems,
    bool ArtworkAvailable,
    uint ArtworkHighWaterMark,
    Dictionary<long, ArtworkReference> TrackArtwork,
    Dictionary<long, TrackMetadata?> TrackMetadata,
    Dictionary<long, TrackMetadata?> ProtectedMetadata)
{
    public CatalogueGroup[]? AlbumGroups { get; init; }

    public CatalogueGroup[]? ArtistGroups { get; init; }

    public static CatalogueCheckpoint Capture(DeviceCatalogue catalogue)
    {
        return new CatalogueCheckpoint(
            catalogue.DatabaseId,
            catalogue.ContainerId,
            catalogue.Anchor,
            [.. catalogue.Tracks],
            new Dictionary<string, long>(catalogue.Albums),
            new Dictionary<string, long>(catalogue.Artists),
            [.. catalogue.OccupiedIds],
            catalogue.SortNames,
            [.. catalogue.ProtectedItems],
            catalogue.ArtworkAvailable,
            catalogue.ArtworkHighWaterMark,
            new Dictionary<long, ArtworkReference>(catalogue.TrackArtwork),
            catalogue.Tracks.ToDictionary(track => track.Id, track => track.Metadata),
            catalogue.ProtectedItems.ToDictionary(item => item.ItemId, item => item.Metadata))
        {
            AlbumGroups = catalogue.AlbumGroups is { } albums ? [.. albums] : null,
            ArtistGroups = catalogue.ArtistGroups is { } artists ? [.. artists] : null
        };
    }

    public DeviceCatalogue Restore()
    {
        return new DeviceCatalogue(
            this.DatabaseId,
            this.ContainerId,
            this.Anchor,
            [.. this.Tracks.Select(track => track with { Metadata = this.TrackMetadata.GetValueOrDefault(track.Id) })],
            RestoreIds(this.Albums),
            RestoreIds(this.Artists),
            this.OccupiedIds.ToHashSet())
        {
            AlbumGroups = this.AlbumGroups,
            ArtistGroups = this.ArtistGroups,
            SortNames = new GroupSortNames(
                RestoreSortNames(this.SortNames.Albums),
                RestoreSortNames(this.SortNames.AlbumArtists),
                RestoreSortNames(this.SortNames.Artists),
                RestoreSortNames(this.SortNames.Composers)),
            ProtectedItems =
            [
                .. this.ProtectedItems.Select(item => item with
                {
                    Metadata = this.ProtectedMetadata.GetValueOrDefault(item.ItemId)
                })
            ],
            ArtworkAvailable = this.ArtworkAvailable,
            ArtworkHighWaterMark = this.ArtworkHighWaterMark,
            TrackArtwork = this.TrackArtwork
        };
    }

    private static Dictionary<string, long> RestoreIds(IReadOnlyDictionary<string, long> saved)
    {
        var result = new Dictionary<string, long>(GroupNameComparer.Instance);
        foreach (var (name, id) in saved)
        {
            if (!result.TryAdd(name, id)) result[name] = 0;
        }

        return result;
    }

    private static Dictionary<string, string?> RestoreSortNames(IReadOnlyDictionary<string, string?> saved)
    {
        var result = new Dictionary<string, string?>(GroupNameComparer.Instance);
        foreach (var (name, sort) in saved)
        {
            if (!result.TryAdd(name, sort) && !StringComparer.Ordinal.Equals(result[name], sort)) result[name] = null;
        }

        return result;
    }
}
