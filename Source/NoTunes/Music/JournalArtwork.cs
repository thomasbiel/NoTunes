using System.Security.Cryptography;

namespace NoTunes.Music;

internal sealed record JournalArtwork(uint CacheId, int Length, string Sha256)
{
    public ArtworkImage Restore(string path)
    {
        var length = new FileInfo(path).Length;
        if (length != this.Length || length > ArtworkImage.MaximumBytes)
            throw new IOException("Durable artwork size differs from its recovery record.");

        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != this.Sha256)
            throw new IOException("Durable artwork bytes differ from their recovery record.");

        return new ArtworkImage(bytes, this.CacheId);
    }
}
