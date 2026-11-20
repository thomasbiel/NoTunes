using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class DeviceHashCache
{
    internal const string DirectoryName = "device-hash-cache";
    private const int FormatVersion = 1;
    private const string CacheName = "Device hash cache";
    private const long MaximumCacheBytes = 64L * 1024 * 1024;
    private const string CacheExtension = ".json";
    private readonly DeviceHashSnapshot context;
    private readonly string? path;
    private readonly TextWriter error;
    private readonly bool fullReadback;
    private Dictionary<long, DeviceHashEntry> cached = [];
    private readonly Dictionary<long, DeviceHashEntry> verified = [];
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>?>? attributes;

    internal TextWriter Debug { get; set; } = TextWriter.Null;

    internal int Hits { get; private set; }

    internal int Checks { get; private set; }

    internal string? FilePath => this.path;

    private DeviceHashCache(DeviceHashSnapshot context, string? path, TextWriter error, bool fullReadback)
    {
        this.context = context;
        this.path = path;
        this.error = error;
        this.fullReadback = fullReadback;
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
            error,
            fullReadback);

        if (!fullReadback) cache.Load();

        return cache;
    }

    internal async Task PrefetchAsync(
        AfcClient afc,
        SourceLibrary? source,
        DeviceCatalogue catalogue,
        CancellationToken cancellationToken)
    {
        var sizes = source?.Tracks.Select(track => track.Size).ToHashSet();
        var paths = catalogue.Tracks.Where(track => !track.IsIncomplete
                && (sizes is null || SyncPlanner.IsHashCandidate(track, sizes)))
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
            return previous.Hash;
        }

        var hash = await afc.HashFileAsync(
            devicePath,
            track.Size,
            cancellationToken,
            info,
            this.fullReadback);

        this.Checks++;
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

        JsonCacheFile.Write(
            this.path,
            this.context with { Tracks = this.verified.Values.ToArray() },
            this.error,
            CacheName,
            MaximumCacheBytes);
    }

    private void Load()
    {
        if (this.path is null) return;

        this.cached = JsonCacheFile.Read(
            this.path,
            this.ReadSnapshot,
            this.error,
            CacheName,
            MaximumCacheBytes) ?? [];
    }

    private Dictionary<long, DeviceHashEntry> ReadSnapshot(Stream file)
    {
        var snapshot = JsonSerializer.Deserialize<DeviceHashSnapshot>(file);
        if (snapshot is null || snapshot.Version != FormatVersion || snapshot.Tracks is null)
        {
            throw new InvalidDataException("Invalid device hash cache header.");
        }

        var tracks = new Dictionary<long, DeviceHashEntry>();
        if (snapshot.Device != this.context.Device || snapshot.Identity != this.context.Identity
            || snapshot.DatabaseId != this.context.DatabaseId || snapshot.ContainerId != this.context.ContainerId)
        {
            return tracks;
        }

        foreach (var track in snapshot.Tracks)
        {
            if (track is null || track.Id == 0 || string.IsNullOrEmpty(track.Path) || track.Size < 0
                || !ContentHash.IsValid(track.Hash) || track.Attributes is null
                || !HasModificationTime(track.Attributes)
                || AfcClient.ValidateFileInfo(track.Path, track.Attributes, track.Size) != track.Size
                || !tracks.TryAdd(track.Id, track with { Hash = track.Hash.ToUpperInvariant() }))
            {
                throw new InvalidDataException("Invalid device hash cache entry.");
            }
        }

        return tracks;
    }

    private static bool HasModificationTime(IReadOnlyDictionary<string, string> attributes)
    {
        return ProtocolNumbers.TryParseNonNegativeInt64(attributes.GetValueOrDefault(AfcKeys.ModificationTime), out _);
    }
}
