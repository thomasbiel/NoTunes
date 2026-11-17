namespace NoTunes.Music;

internal sealed record ProtectedCatalogueItem(
    long ItemId,
    long? SyncId,
    long? StoreSagaId,
    long? MediaType,
    long? RemoteLocationId,
    string? Title,
    string? BasePath,
    string? Location,
    long? Size)
{
    internal TrackMetadata? Metadata { get; init; }
}
