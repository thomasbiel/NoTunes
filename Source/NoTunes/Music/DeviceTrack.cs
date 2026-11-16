namespace NoTunes.Music;

public sealed record DeviceTrack(long Id, string Title, string? Path, long Size)
{
    public bool IsIncomplete => this.Path is null;
}
