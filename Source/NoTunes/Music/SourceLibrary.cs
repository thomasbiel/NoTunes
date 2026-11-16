namespace NoTunes.Music;

public sealed record SourceLibrary(string Root, IReadOnlyList<SourceTrack> Tracks)
{
    public long TotalBytes => this.Tracks.Sum(t => t.Size);
}
