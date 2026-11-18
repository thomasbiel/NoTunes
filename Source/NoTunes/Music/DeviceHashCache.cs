using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class DeviceHashCache
{
    internal const string DirectoryName = "device-hash-cache";
    private const int FormatVersion = 1;
    private const int Sha256HexLength = 64;
    private const long MaximumCacheBytes = 64L * 1024 * 1024;
    private const string CacheExtension = ".json";
    private const string TemporaryExtension = ".tmp";
    private readonly DeviceHashSnapshot context;
    private readonly string? path;
    private readonly TextWriter error;
    private readonly Dictionary<long, DeviceHashEntry> cached = [];
    private readonly Dictionary<long, DeviceHashEntry> verified = [];
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>?>? attributes;

    internal TextWriter Debug { get; set; } = TextWriter.Null;

    internal int Hits { get; private set; }

    internal int Reads { get; private set; }

    internal string? FilePath => this.path;

    private DeviceHashCache(DeviceHashSnapshot context, string? path, TextWriter error)
    {
        this.context = context;
        this.path = path;
        this.error = error;
    }

    internal static DeviceHashCache Open(
        string? directory,
        string device,
        LibraryIdentity identity,
        DeviceCatalogue catalogue,
        bool fullReadback,
        TextWriter error)
    {
        var normalized = DeviceUdid.Normalize(device).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var context = new DeviceHashSnapshot(
            FormatVersion,
            normalized,
            identity,
            catalogue.DatabaseId,
            catalogue.ContainerId,
            []);
        var cache = new DeviceHashCache(
            context,
            directory is null ? null : System.IO.Path.Combine(directory, key + CacheExtension),
            error);
        if (!fullReadback) cache.Load();

        return cache;
    }

    internal async Task PrefetchAsync(
        AfcClient afc,
        SourceLibrary source,
        DeviceCatalogue catalogue,
        CancellationToken cancellationToken)
    {
        var sizes = source.Tracks.Select(track => track.Size).ToHashSet();
        var paths = catalogue.Tracks.Where(track => !track.IsIncomplete && sizes.Contains(track.Size))
            .Select(track => track.Path!).Distinct(StringComparer.Ordinal).ToArray();
        var progress = new OperationProgress(this.error, "Reading iPhone file attributes", paths.Length);
        this.attributes = await afc.GetFileInfosAsync(paths, cancellationToken, progress.Report);
    }

    internal async Task<string?> ReadAsync(AfcClient afc, DeviceTrack track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (track.Path is not { } devicePath) return null;

        await this.Debug.WriteLineAsync($"Compare: {track.Title}");
        var info = this.attributes is not null && this.attributes.TryGetValue(devicePath, out var prefetched)
            ? prefetched
            : await afc.GetFileInfoAsync(devicePath, cancellationToken);
        if (info is null) return null;

        if (AfcClient.ValidateFileInfo(devicePath, info, track.Size) != track.Size)
        {
            throw new IOException($"The catalogue file size disagrees with the iPhone file: {track.Title}");
        }

        var reusable = HasModificationTime(info);
        if (reusable && this.cached.TryGetValue(track.Id, out var previous)
            && StringComparer.Ordinal.Equals(previous.Path, devicePath) && previous.Size == track.Size
            && AfcClient.SameAttributes(previous.Attributes, info))
        {
            this.verified[track.Id] = previous;
            this.Hits++;
            return previous.Sha256;
        }

        var hash = await afc.HashFileAsync(
            devicePath,
            track.Size,
            cancellationToken,
            info);
        this.Reads++;
        if (reusable)
        {
            this.verified[track.Id] = new DeviceHashEntry(
                track.Id,
                devicePath,
                track.Size,
                hash,
                info.Where(pair => pair.Key != AfcKeys.AccessTime)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        return hash;
    }

    internal void Save()
    {
        if (this.path is null) return;

        var temporary = this.path + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(this.path)!);
            using (var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(file, this.context with { Tracks = this.verified.Values.ToArray() });
                if (file.Length > MaximumCacheBytes) throw new InvalidDataException("Device hash cache exceeds its size limit.");

                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, this.path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            this.error.WriteLine($"Device hash cache could not be saved: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                this.error.WriteLine($"Temporary device hash cache could not be removed: {exception.Message}");
            }
        }
    }

    private void Load()
    {
        if (this.path is null) return;

        try
        {
            using var file = File.OpenRead(this.path);
            if (file.Length > MaximumCacheBytes) throw new InvalidDataException("Device hash cache exceeds its size limit.");

            var snapshot = JsonSerializer.Deserialize<DeviceHashSnapshot>(file);
            if (snapshot is null || snapshot.Version != FormatVersion || snapshot.Tracks is null)
            {
                throw new InvalidDataException("Invalid device hash cache header.");
            }

            if (snapshot.Device != this.context.Device || snapshot.Identity != this.context.Identity
                || snapshot.DatabaseId != this.context.DatabaseId || snapshot.ContainerId != this.context.ContainerId)
            {
                return;
            }

            foreach (var track in snapshot.Tracks)
            {
                if (track is null || track.Id == 0 || string.IsNullOrEmpty(track.Path) || track.Size < 0
                    || track.Sha256 is null || track.Sha256.Length != Sha256HexLength
                    || !track.Sha256.All(char.IsAsciiHexDigit) || track.Attributes is null
                    || !HasModificationTime(track.Attributes)
                    || AfcClient.ValidateFileInfo(track.Path, track.Attributes, track.Size) != track.Size
                    || !this.cached.TryAdd(track.Id, track with { Sha256 = track.Sha256.ToUpperInvariant() }))
                {
                    throw new InvalidDataException("Invalid device hash cache entry.");
                }
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            this.cached.Clear();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            this.cached.Clear();
            this.error.WriteLine($"Device hash cache could not be read; files will be hashed again: {exception.Message}");
        }
    }

    private static bool HasModificationTime(IReadOnlyDictionary<string, string> attributes)
    {
        return ProtocolNumbers.TryParseNonNegativeInt64(attributes.GetValueOrDefault(AfcKeys.ModificationTime), out _);
    }
}
