using System.Security.Cryptography;
using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class ArtworkCache(string directory, int maximumEntries = ArtworkCache.DefaultMaximumEntries)
{
    internal const int DefaultMaximumEntries = 128;
    private readonly Dictionary<string, ArtworkImage> images = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly int capacity = RequireCapacity(maximumEntries);

    public ArtworkImage Read(byte[] bytes)
    {
        if (bytes.Length > ArtworkImage.MaximumBytes)
            throw new InvalidDataException("Embedded cover exceeds its size limit.");

        var key = Convert.ToHexString(SHA256.HashData(bytes));
        if (this.images.TryGetValue(key, out var image)) return image;

        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + SyncPaths.JpegExtension);
        image = ArtworkImage.Read(bytes).Store(path);
        if (this.images.Count == this.capacity) this.images.Remove(this.order.Dequeue());

        this.images.Add(key, image);
        this.order.Enqueue(key);
        return image;
    }

    private static int RequireCapacity(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

        return value;
    }
}
