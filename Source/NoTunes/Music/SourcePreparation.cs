using System.Security.Cryptography;
using NoTunes.Apple;
using NoTunes.Platform;

namespace NoTunes.Music;

internal static class SourcePreparation
{
    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        SourceLibrary library,
        SyncPlan plan,
        DeviceCatalogue catalogue,
        ISourceFileSystem fileSystem,
        SyncWorkspace workspace,
        CancellationToken cancellationToken)
    {
        using var session = fileSystem.OpenSource(new DirectoryInfo(library.Root), cancellationToken);
        var result = new List<PreparedTrack>();
        var ids = catalogue.OccupiedIds.ToHashSet();
        var albums = new Dictionary<string, long>(catalogue.Albums, StringComparer.Ordinal);
        var artists = new Dictionary<string, long>(catalogue.Artists, StringComparer.Ordinal);
        foreach (var track in plan.Add)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(library.Root, track.RelativePath));
            if (!fullPath.StartsWith(Path.TrimEndingDirectorySeparator(library.Root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Source track escapes the library root.");

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

                var localPath = Path.Combine(workspace.Root, Guid.NewGuid().ToString("N") + SyncPaths.Mp3Extension);
                await using (var input = session.OpenRead(new FileInfo(fullPath)))
                await using (var output = new FileStream(
                    localPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    if (input.Length != track.Size || File.GetLastWriteTimeUtc(input.SafeFileHandle) != track.LastWriteTimeUtc)
                        throw new IOException($"Source changed after scanning: {track.RelativePath}");

                    await input.CopyToAsync(output, cancellationToken);
                    if (input.Length != track.Size || File.GetLastWriteTimeUtc(input.SafeFileHandle) != track.LastWriteTimeUtc)
                        throw new IOException($"Source changed during preparation: {track.RelativePath}");
                }

                await using (var copy = File.OpenRead(localPath))
                {
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken));
                    if (copy.Length != track.Size || !StringComparer.Ordinal.Equals(actual, track.Sha256))
                        throw new IOException($"Source bytes changed after scanning: {track.RelativePath}");
                }

                var metadata = Mp3Metadata.Read(localPath, track.RelativePath);
                var artistId = GroupId(artists, metadata.AlbumArtist, ids);
                var albumId = GroupId(albums, CatalogueReader.GroupKey(metadata.Album, metadata.AlbumArtist), ids);
                result.Add(new PreparedTrack(
                    track,
                    localPath,
                    metadata,
                    NewId(ids),
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

    private static long GroupId(Dictionary<string, long> groups, string name, HashSet<long> ids)
    {
        if (!groups.TryGetValue(name, out var id) || id == 0)
            groups[name] = id = NewId(ids);
        return id;
    }

    private static long NewId(HashSet<long> ids)
    {
        while (true)
        {
            var id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long)));
            if ((ulong)id >= SyncProtocol.MinimumPersistentId && ids.Add(id))
                return id;
        }
    }
}
