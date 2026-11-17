using System.Security.Cryptography;
using NoTunes.Apple;
using NoTunes.Platform;

namespace NoTunes.Music;

internal static class SourcePreparation
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() 
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    
    private const long UnusedGroupId = 0;

    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        SourceLibrary library,
        SyncPlan plan,
        DeviceCatalogue catalogue,
        ISourceFileSystem fileSystem,
        SyncWorkspace workspace,
        CancellationToken cancellationToken,
        bool artworkOnly = false)
    {
        using var session = fileSystem.OpenSource(new DirectoryInfo(library.Root), cancellationToken);
        var artworkCache = new ArtworkCache(workspace.Root);
        var result = new List<PreparedTrack>();
        var ids = catalogue.OccupiedIds.ToHashSet();
        var artworkId = catalogue.ArtworkHighWaterMark;
        var albums = new Dictionary<string, long>(catalogue.Albums, GroupNameComparer.Instance);
        var artists = new Dictionary<string, long>(catalogue.Artists, GroupNameComparer.Instance);
        var albumSorts = new Dictionary<string, string?>(catalogue.SortNames.Albums, GroupNameComparer.Instance);
        var albumArtistSorts = new Dictionary<string, string?>(catalogue.SortNames.AlbumArtists, GroupNameComparer.Instance);
        var artistSorts = new Dictionary<string, string?>(catalogue.SortNames.Artists, GroupNameComparer.Instance);
        var composerSorts = new Dictionary<string, string?>(catalogue.SortNames.Composers, GroupNameComparer.Instance);
        var rootPrefix = Path.EndsInDirectorySeparator(library.Root)
            ? library.Root : library.Root + Path.DirectorySeparatorChar;
        
        foreach (var track in plan.Add)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(library.Root, track.RelativePath));
            if (!fullPath.StartsWith(rootPrefix, PathComparison)) throw new IOException("Source track escapes the library root.");

            var parents = Path.GetRelativePath(library.Root, Path.GetDirectoryName(fullPath)!).Split(Path.DirectorySeparatorChar);
            var parent = library.Root;
            var held = new List<DirectoryInfo>();
            try
            {
                foreach (var part in parents.Where(part => part != AfcKeys.CurrentDirectory))
                {
                    parent = Path.Combine(parent, part);
                    var directory = new DirectoryInfo(parent);
                    session.AddDirectory(directory);
                    held.Add(directory);
                }

                if (artworkOnly)
                {
                    await using var guarded = session.OpenRead(new FileInfo(fullPath));
                    var hasArtwork = Mp3Metadata.HasArtwork(guarded);
                    if (guarded.Length != track.Size || File.GetLastWriteTimeUtc(guarded.SafeFileHandle) != track.LastWriteTimeUtc)
                    {
                        throw new IOException($"Source changed while checking artwork: {track.RelativePath}");
                    }

                    if (!hasArtwork) continue;
                }

                var localPath = Path.Combine(workspace.Root, Guid.NewGuid().ToString("N") + SyncPaths.Mp3Extension);
                await using (var input = session.OpenRead(new FileInfo(fullPath)))
                await using (var output = new FileStream(
                    localPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    if (input.Length != track.Size || File.GetLastWriteTimeUtc(input.SafeFileHandle) != track.LastWriteTimeUtc)
                    {
                        throw new IOException($"Source changed after scanning: {track.RelativePath}");
                    }

                    await input.CopyToAsync(output, cancellationToken);
                    if (input.Length != track.Size || File.GetLastWriteTimeUtc(input.SafeFileHandle) != track.LastWriteTimeUtc)
                    {
                        throw new IOException($"Source changed during preparation: {track.RelativePath}");
                    }
                }

                await using (var copy = File.OpenRead(localPath))
                {
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken));
                    if (copy.Length != track.Size || !StringComparer.Ordinal.Equals(actual, track.Sha256))
                    {
                        throw new IOException($"Source bytes changed after scanning: {track.RelativePath}");
                    }
                }

                var metadata = Mp3Metadata.Read(localPath, track.RelativePath, artworkCache);
                if (artworkOnly && metadata.Artwork is null)
                {
                    File.Delete(localPath);
                    continue;
                }

                if (metadata.Artwork is { } artwork)
                {
                    if (!catalogue.ArtworkAvailable) throw new IOException("This catalogue cannot register artwork.");

                    metadata = metadata with { Artwork = artwork with { CacheId = ArtworkImage.Allocate(ref artworkId) } };
                    metadata.Item[MediaKeys.ArtworkChecksum] = (long)artwork.Checksum;
                    metadata.Item[MediaKeys.ArtworkCacheId] = (long)artworkId;
                }

                var artistId = UnusedGroupId;
                var albumId = UnusedGroupId;
                if (!artworkOnly)
                {
                    artistId = GroupId(
                        artists,
                        metadata.AlbumArtist,
                        ids,
                        catalogue.ArtistGroups);
                    
                    albumId = GroupId(
                        albums,
                        CatalogueReader.GroupKey(metadata.Album, metadata.AlbumArtist),
                        ids,
                        catalogue.AlbumGroups);
                    
                    PreserveSortName(
                        albumSorts,
                        CatalogueReader.GroupKey(metadata.Album, metadata.AlbumArtist),
                        metadata.Item,
                        MediaKeys.SortAlbum);
                    
                    PreserveSortName(
                        albumArtistSorts,
                        metadata.AlbumArtist,
                        metadata.Item,
                        MediaKeys.SortAlbumArtist);
                    
                    PreserveSortName(
                        artistSorts,
                        (string)metadata.Item[MediaKeys.Artist],
                        metadata.Item,
                        MediaKeys.SortArtist);
                    
                    if (metadata.Item.TryGetValue(MediaKeys.Composer, out var composer))
                    {
                        PreserveSortName(
                            composerSorts,
                            (string)composer,
                            metadata.Item,
                            MediaKeys.SortComposer);
                    }
                }

                result.Add(new PreparedTrack(
                    track,
                    localPath,
                    metadata,
                    LibraryPreferences.NewId(ids),
                    albumId,
                    artistId));
            }
            finally
            {
                foreach (var directory in held.AsEnumerable().Reverse())
                {
                    session.ReleaseDirectory(directory);
                }
            }
        }

        return result;
    }

    private static long GroupId(
        Dictionary<string, long> groups,
        string name,
        HashSet<long> ids,
        IReadOnlyList<CatalogueGroup>? rows)
    {
        if (groups.TryGetValue(name, out var id) && id == 0)
        {
            var candidates = rows ?? [];
            var conflictingIds = candidates
                .Where(group => GroupNameComparer.Instance.Equals(group.Key, name))
                .Select(group => group.SyncId).Where(syncId => syncId != 0)
                .ToHashSet();
            
            var conflicts = candidates
                .Where(group => conflictingIds.Contains(group.SyncId))
                .Select(group => $"'{group.DisplayName}' (sync ID {group.SyncId})")
                .Distinct(StringComparer.Ordinal);
            
            var details = string.Join(", ", conflicts);
            throw new InvalidDataException("Cannot add music using ambiguous group '"
                + name.Replace(CatalogueReader.GroupKeySeparator, '/')
                + "'. " + (details.Length == 0 ? "No unique insertion target is available."
                    : "Conflicting entries: " + details));
        }

        if (!groups.ContainsKey(name))
        {
            groups[name] = id = LibraryPreferences.NewId(ids);
        }

        return id;
    }

    private static void PreserveSortName(
        Dictionary<string, string?> groups,
        string name,
        Dictionary<string, object> item,
        string key)
    {
        if (!groups.TryGetValue(name, out var sort))
        {
            sort = (string)item[key];
            groups.Add(name, sort);
        }

        item[key] = sort ?? throw new InvalidDataException("Ambiguous music group sort name.");
    }

}
