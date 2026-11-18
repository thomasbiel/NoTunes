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
        bool artworkOnly = false,
        GroupNamePrompt? prompt = null,
        Action<SkippedSourceFile>? onSkipped = null,
        TextWriter? error = null)
    {
        using var session = fileSystem.OpenSource(new DirectoryInfo(library.Root), cancellationToken);
        var artworkCache = new ArtworkCache(workspace.Root);
        var result = new List<PreparedTrack>();
        var progress = new OperationProgress(
            error ?? TextWriter.Null,
            artworkOnly ? "Checking and preparing covers" : "Preparing source tracks",
            plan.Add.Count);

        var ids = catalogue.OccupiedIds.ToHashSet();
        var artworkId = catalogue.ArtworkHighWaterMark;

        var artists = Resolver(
            MusicGroupKind.AlbumArtist,
            catalogue.ArtistGroups,
            catalogue.Artists,
            catalogue.SortNames.AlbumArtists);

        var albums = Resolver(
            MusicGroupKind.Album,
            catalogue.AlbumGroups,
            catalogue.Albums,
            catalogue.SortNames.Albums);

        var performers = Resolver(
            MusicGroupKind.Artist,
            null,
            null,
            catalogue.SortNames.Artists);

        var composers = Resolver(
            MusicGroupKind.Composer,
            null,
            null,
            catalogue.SortNames.Composers);

        var rootPrefix = Path.EndsInDirectorySeparator(library.Root) ? library.Root : library.Root + Path.DirectorySeparatorChar;

        var completed = 0;
        foreach (var track in plan.Add)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(completed++);
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
                    var readable = TryReadMetadata(track, () => Mp3Metadata.HasArtwork(guarded), out var hasArtwork);
                    if (guarded.Length != track.Size || File.GetLastWriteTimeUtc(guarded.SafeFileHandle) != track.LastWriteTimeUtc)
                    {
                        throw new IOException($"Source changed while checking artwork: {track.RelativePath}");
                    }

                    if (!readable || !hasArtwork) continue;
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

                if (!TryReadMetadata(
                    track,
                    () => Mp3Metadata.Read(localPath, track.RelativePath, artworkCache),
                    out var metadata))
                {
                    File.Delete(localPath);
                    continue;
                }

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
                    var artist = await artists.ResolveAsync(
                        metadata.AlbumArtist,
                        (string)metadata.Item[MediaKeys.SortAlbumArtist],
                        track.RelativePath,
                        null,
                        cancellationToken);

                    var album = await albums.ResolveAsync(
                        metadata.Album,
                        (string)metadata.Item[MediaKeys.SortAlbum],
                        track.RelativePath,
                        artist.Name,
                        cancellationToken);

                    var performer = await performers.ResolveAsync(
                        (string)metadata.Item[MediaKeys.Artist],
                        (string)metadata.Item[MediaKeys.SortArtist],
                        track.RelativePath,
                        null,
                        cancellationToken);

                    metadata = metadata with { Album = album.Name, AlbumArtist = artist.Name };
                    metadata.Item[MediaKeys.Album] = album.Name;
                    metadata.Item[MediaKeys.AlbumArtist] = artist.Name;
                    metadata.Item[MediaKeys.SortAlbum] = album.SortName;
                    metadata.Item[MediaKeys.SortAlbumArtist] = artist.SortName;
                    metadata.Item[MediaKeys.Artist] = performer.Name;
                    metadata.Item[MediaKeys.SortArtist] = performer.SortName;
                    artistId = artist.Id;
                    albumId = album.Id;

                    if (metadata.Item.TryGetValue(MediaKeys.Composer, out var composer))
                    {
                        var resolvedComposer = await composers.ResolveAsync(
                            (string)composer,
                            (string)metadata.Item[MediaKeys.SortComposer],
                            track.RelativePath,
                            null,
                            cancellationToken);

                        metadata.Item[MediaKeys.Composer] = resolvedComposer.Name;
                        metadata.Item[MediaKeys.SortComposer] = resolvedComposer.SortName;
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

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(completed);
        return result;

        bool TryReadMetadata<T>(SourceTrack track, Func<T> read, out T metadata)
        {
            try
            {
                metadata = read();
                return true;
            }
            catch (Exception exception) when (onSkipped is not null
                && exception is InvalidDataException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                onSkipped(new SkippedSourceFile(track.RelativePath, exception.GetBaseException().Message, artworkOnly));
                metadata = default!;
                return false;
            }
        }

        MusicGroupResolver Resolver(
            MusicGroupKind kind,
            IReadOnlyList<CatalogueGroup>? rows,
            IReadOnlyDictionary<string, long>? existingIds,
            IReadOnlyDictionary<string, string?> sorts)
        {
            return new MusicGroupResolver(
                kind,
                rows,
                existingIds,
                sorts,
                ids,
                prompt);
        }
    }
}
