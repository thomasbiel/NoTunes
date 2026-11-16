namespace NoTunes.Music;

public sealed record SyncPlan(
    string Device,
    uint Anchor,
    IReadOnlyList<SourceTrack> Add,
    IReadOnlyList<DeviceTrack> Delete,
    int Unchanged)
{
    public long TransferBytes => this.Add.Sum(track => track.Size);

    public bool HasChanges => this.Add.Count != 0 || this.Delete.Count != 0;
}
