namespace NoTunes.Music;

public sealed record SyncPlan(
    string Device,
    uint Anchor,
    IReadOnlyList<SourceTrack> Add,
    IReadOnlyList<DeviceTrack> Delete,
    int Unchanged)
{
    internal IReadOnlyList<SourceMatch> Matches { get; init; } = [];

    public IReadOnlyList<SourceTrack> Artwork { get; init; } = [];

    public IReadOnlyList<SkippedSourceFile> Skipped { get; init; } = [];

    public IReadOnlyList<DeviceTrack> DeferredDeletions { get; init; } = [];

    public long TransferBytes => this.Add.Sum(track => track.Size);

    public bool HasChanges => this.Add.Count != 0 || this.Delete.Count != 0 || this.Artwork.Count != 0;
}
