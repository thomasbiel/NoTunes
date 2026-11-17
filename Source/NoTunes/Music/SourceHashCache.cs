using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoTunes.Music;

public sealed class SourceHashCache(string directory, TextWriter error)
{
    internal const string DirectoryName = "hash-cache";
    private const string CacheExtension = ".json";
    private const string TemporaryExtension = ".tmp";
    private const int FormatVersion = 1;
    private const int Sha256HexLength = 64;

    internal string DirectoryPath { get; } = directory;

    internal IReadOnlyDictionary<string, SourceTrack> Load(string root)
    {
        var tracks = new Dictionary<string, SourceTrack>(StringComparer.Ordinal);
        try
        {
            using var file = File.OpenRead(this.PathFor(root));
            var snapshot = JsonSerializer.Deserialize<SourceHashSnapshot>(file);
            if (snapshot is null || snapshot.Version != FormatVersion || snapshot.Root != root || snapshot.Tracks is null)
            {
                throw new InvalidDataException("Invalid source hash cache header.");
            }

            foreach (var track in snapshot.Tracks)
            {
                if (track is null || string.IsNullOrEmpty(track.RelativePath) || track.Size < 0
                    || track.Sha256 is null || track.Sha256.Length != Sha256HexLength
                    || !track.Sha256.All(char.IsAsciiHexDigit) || !tracks.TryAdd(track.RelativePath, track))
                {
                    throw new InvalidDataException("Invalid source hash cache entry.");
                }
            }

            return tracks;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Dictionary<string, SourceTrack>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine($"Source hash cache could not be read; files will be hashed again: {exception.Message}");
            return new Dictionary<string, SourceTrack>(StringComparer.Ordinal);
        }
    }

    internal void Save(SourceLibrary library)
    {
        var path = this.PathFor(library.Root);
        var temporary = path + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try
        {
            Directory.CreateDirectory(this.DirectoryPath);
            using (var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(file, new SourceHashSnapshot(FormatVersion, library.Root, library.Tracks));
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Source hash cache could not be saved: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Temporary source hash cache could not be removed: {exception.Message}");
            }
        }
    }

    internal string PathFor(string root)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        return Path.Combine(this.DirectoryPath, key + CacheExtension);
    }
}
