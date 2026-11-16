using System.Security.Cryptography;
using NoTunes.Platform;

namespace NoTunes.Music;

public sealed record SourceTrack(string RelativePath, long Size, DateTime LastWriteTimeUtc, string Sha256);
public sealed record SourceLibrary(string Root, IReadOnlyList<SourceTrack> Tracks)
{
    public long TotalBytes => Tracks.Sum(t => t.Size);
}

public sealed class SourceScanner(ISourceFileSystem fileSystem)
{
    private const string Mp3Extension = ".mp3";
    public async Task<SourceLibrary> ScanAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = new DirectoryInfo(Path.GetFullPath(folder));
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(root.FullName); }
        catch (FileNotFoundException e)
        {
            throw new DirectoryNotFoundException($"Source folder not found: {root.FullName}", e);
        }
        if (!rootAttributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"The source path is not a directory: {root.FullName}");
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The source folder must not be a symbolic link or directory junction.");

        using var paths = fileSystem.OpenSource(root, cancellationToken);
        var tracks = new List<SourceTrack>();
        var directories = new Stack<DirectoryInfo>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in paths.EnumerateDirectory(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Symbolic link or junction in source folder: {Path.Combine(directory.FullName, entry.Name)}. " +
                        "The scan is aborted to prevent an incomplete inventory from causing deletions.");
                var fullName = Path.Combine(directory.FullName, entry.Name);
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    var child = new DirectoryInfo(fullName);
                    paths.AddDirectory(child);
                    directories.Push(child);
                    continue;
                }
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(entry.Name), Mp3Extension))
                    continue;

                await using var stream = paths.OpenRead(new FileInfo(fullName));
                var size = stream.Length;
                var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                if (stream.Length != size || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != modified)
                    throw new IOException($"File changed during the scan: {fullName}");
                tracks.Add(new SourceTrack(Path.GetRelativePath(root.FullName, fullName), size,
                    modified, Convert.ToHexString(hash)));
            }
        }
        tracks.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new SourceLibrary(root.FullName, tracks);
    }
}
