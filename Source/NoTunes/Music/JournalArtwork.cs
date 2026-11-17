namespace NoTunes.Music;

internal sealed record JournalArtwork(uint CacheId, int Length, string Sha256)
{
    public ArtworkImage Restore(string path)
    {
        var file = new ArtworkFile(path, this.Length, this.Sha256);
        file.Validate();
        return new ArtworkImage(file, this.CacheId);
    }
}
