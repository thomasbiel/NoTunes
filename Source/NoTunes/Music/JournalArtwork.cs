using System.Text.Json.Serialization;

namespace NoTunes.Music;

internal sealed record JournalArtwork(
    uint CacheId,
    int Length,
    [property: JsonPropertyName(ContentHash.AlgorithmName)] string Hash)
{
    public ArtworkImage Restore(string path)
    {
        var file = new ArtworkFile(path, this.Length, this.Hash);
        file.Validate();
        return new ArtworkImage(file, this.CacheId);
    }
}
