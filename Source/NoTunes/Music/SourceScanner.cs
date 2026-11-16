using System.Security.Cryptography;
using NoTunes.Platform;

namespace NoTunes.Music;

public sealed class SourceScanner(ISourceFileSystem fileSystem)
{
    private const string Mp3Extension = ".mp3";

    public async Task<SourceLibrary> ScanAsync(string folder, CancellationToken cancellationToken)
    {
        var root = GetSourceDirectory(folder);
        using var paths = fileSystem.OpenSource(root, cancellationToken);
        var tracks = new List<SourceTrack>();
        var directories = new Stack<(DirectoryInfo Directory, IEnumerator<SourceEntry> Entries)>();
        directories.Push((root, paths.EnumerateDirectory(root).GetEnumerator()));
        try
        {
            while (directories.TryPeek(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!current.Entries.MoveNext())
                {
                    directories.Pop();
                    current.Entries.Dispose();
                    paths.ReleaseDirectory(current.Directory);
                    continue;
                }

                var directory = current.Directory;
                var entry = current.Entries.Current;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Symbolic link or junction in source folder: {Path.Combine(directory.FullName, entry.Name)}. " + "The scan is aborted to prevent an incomplete inventory from causing deletions.");

                var fullName = Path.Combine(directory.FullName, entry.Name);
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    var child = new DirectoryInfo(fullName);
                    paths.AddDirectory(child);
                    directories.Push((child, paths.EnumerateDirectory(child).GetEnumerator()));
                    continue;
                }

                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(entry.Name), Mp3Extension))
                    continue;
                tracks.Add(await ReadTrackAsync(
                    paths,
                    root,
                    fullName,
                    cancellationToken));
            }
        }
        finally
        {
            foreach (var directory in directories)
                directory.Entries.Dispose();
        }

        tracks.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new SourceLibrary(root.FullName, tracks);
    }

    private static DirectoryInfo GetSourceDirectory(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = new DirectoryInfo(Path.GetFullPath(folder));
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(root.FullName);
        }
        catch (FileNotFoundException e)
        {
            throw new DirectoryNotFoundException($"Source folder not found: {root.FullName}", e);
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"The source path is not a directory: {root.FullName}");
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The source folder must not be a symbolic link or directory junction.");

        return root;
    }

    private static async Task<SourceTrack> ReadTrackAsync(ISourceSession paths, DirectoryInfo root, string fullName, CancellationToken cancellationToken)
    {
        await using var stream = paths.OpenRead(new FileInfo(fullName));
        var size = stream.Length;
        var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (stream.Length != size || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != modified)
            throw new IOException($"File changed during the scan: {fullName}");

        return new SourceTrack(
            Path.GetRelativePath(root.FullName, fullName),
            size,
            modified,
            Convert.ToHexString(hash));
    }
}
