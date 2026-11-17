namespace NoTunes.Music;

public sealed record DeviceTrack(long Id, string Title, string? Path, long Size)
{
    internal TrackMetadata? Metadata { get; init; }

    public bool IsIncomplete => this.Path is null;
}
