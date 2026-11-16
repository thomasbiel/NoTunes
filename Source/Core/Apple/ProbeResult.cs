namespace NoTunes.Apple;

public sealed record ProbeResult
{
    public required DeviceInfo Device { get; init; }
    public required bool Paired { get; init; }
    public required bool AfcAvailable { get; init; }
    public required bool MusicDatabasePresent { get; init; }
    public required bool AtcAvailable { get; init; }
    public required bool SyncAllowed { get; init; }
    public required bool DataProtected { get; init; }
    public required long? MusicTrackCount { get; init; }
    public required long? MusicBytes { get; init; }
    public required string? FreeBytes { get; init; }
    public required bool CatalogueReadVerified { get; init; }
    public required int? CatalogueBytes { get; init; }
    public required int? CatalogueWalBytes { get; init; }
}
