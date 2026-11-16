namespace NoTunes.Apple;

public sealed record AtcSyncStatus
{
    public required bool SyncAllowed { get; init; }

    public required bool DataProtected { get; init; }

    public required long? MusicTrackCount { get; init; }

    public required long? MusicBytes { get; init; }
}
