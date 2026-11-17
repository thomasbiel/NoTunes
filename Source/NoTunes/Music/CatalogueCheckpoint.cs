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
    public static CatalogueCheckpoint Capture(DeviceCatalogue catalogue)
    {
        return new CatalogueCheckpoint(
            catalogue.DatabaseId,
            catalogue.ContainerId,
            catalogue.Anchor,
            catalogue.Tracks.ToArray(),
            new Dictionary<string, long>(catalogue.Albums),
            new Dictionary<string, long>(catalogue.Artists),
            catalogue.OccupiedIds.ToArray(),
            catalogue.SortNames,
            catalogue.ProtectedItems.ToArray(),
            catalogue.ArtworkAvailable,
            catalogue.ArtworkHighWaterMark,
            new Dictionary<long, ArtworkReference>(catalogue.TrackArtwork),
            catalogue.Tracks.ToDictionary(track => track.Id, track => track.Metadata),
            catalogue.ProtectedItems.ToDictionary(item => item.ItemId, item => item.Metadata));
    }

    public DeviceCatalogue Restore()
    {
        return new DeviceCatalogue(
            this.DatabaseId,
            this.ContainerId,
            this.Anchor,
            this.Tracks.Select(track => track with { Metadata = this.TrackMetadata.GetValueOrDefault(track.Id) }).ToArray(),
            new Dictionary<string, long>(this.Albums, GroupNameComparer.Instance),
            new Dictionary<string, long>(this.Artists, GroupNameComparer.Instance),
            this.OccupiedIds.ToHashSet())
        {
            SortNames = new GroupSortNames(
                new Dictionary<string, string?>(this.SortNames.Albums, GroupNameComparer.Instance),
                new Dictionary<string, string?>(this.SortNames.AlbumArtists, GroupNameComparer.Instance),
                new Dictionary<string, string?>(this.SortNames.Artists, GroupNameComparer.Instance),
                new Dictionary<string, string?>(this.SortNames.Composers, GroupNameComparer.Instance)),
            ProtectedItems = this.ProtectedItems.Select(item => item with
            {
                Metadata = this.ProtectedMetadata.GetValueOrDefault(item.ItemId)
            }).ToArray(),
            ArtworkAvailable = this.ArtworkAvailable,
            ArtworkHighWaterMark = this.ArtworkHighWaterMark,
            TrackArtwork = this.TrackArtwork
        };
    }
}
