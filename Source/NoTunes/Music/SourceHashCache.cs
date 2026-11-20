using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoTunes.Music;

public sealed class SourceHashCache(string directory, TextWriter error)
{
    internal const string DirectoryName = "hash-cache";
    private const string CacheExtension = ".json";
    private const string CacheName = "Source hash cache";
    private const int FormatVersion = 1;

    internal string DirectoryPath { get; } = directory;

    internal IReadOnlyDictionary<string, SourceTrack> Load(string root)
    {
        return JsonCacheFile.Read(
            this.PathFor(root),
            file => ReadSnapshot(file, root),
            error,
            CacheName) ?? new Dictionary<string, SourceTrack>(StringComparer.Ordinal);
    }

    private static Dictionary<string, SourceTrack> ReadSnapshot(Stream file, string root)
    {
        var snapshot = JsonSerializer.Deserialize<SourceHashSnapshot>(file);
        if (snapshot is null || snapshot.Version != FormatVersion || snapshot.Root != root || snapshot.Tracks is null)
        {
            throw new InvalidDataException("Invalid source hash cache header.");
        }

        var tracks = new Dictionary<string, SourceTrack>(StringComparer.Ordinal);
        foreach (var track in snapshot.Tracks)
        {
            if (track is null || string.IsNullOrEmpty(track.RelativePath) || track.Size < 0
                || !ContentHash.IsValid(track.Hash) || !tracks.TryAdd(track.RelativePath, track))
            {
                throw new InvalidDataException("Invalid source hash cache entry.");
            }
        }

        return tracks;
    }

    internal void Save(SourceLibrary library)
    {
        JsonCacheFile.Write(
            this.PathFor(library.Root),
            new SourceHashSnapshot(FormatVersion, library.Root, library.Tracks),
            error,
            CacheName);
    }

    internal string PathFor(string root)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        return Path.Combine(this.DirectoryPath, key + CacheExtension);
    }
}
