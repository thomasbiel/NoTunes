using System.Security.Cryptography;

namespace NoTunes.Music;

public sealed record SourceTrack(string RelativePath, long Size, DateTime LastWriteTimeUtc, string Sha256);
public sealed record SourceLibrary(string Root, IReadOnlyList<SourceTrack> Tracks)
{
    public long TotalBytes => Tracks.Sum(t => t.Size);
}

public static class SourceScanner
{
    public static async Task<SourceLibrary> ScanAsync(string folder, CancellationToken cancellationToken)
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

        var tracks = new List<SourceTrack>();
        var directories = new Stack<DirectoryInfo>();
        directories.Push(root);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in directory.EnumerateFileSystemInfos("*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Symbolic link or junction in source folder: {entry.FullName}. " +
                        "The scan is aborted to prevent an incomplete inventory from causing deletions.");
                if (entry is DirectoryInfo child)
                {
                    directories.Push(child);
                    continue;
                }
                if (entry is not FileInfo file || !file.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                    continue;

                file.Refresh();
                var size = file.Length;
                var modified = file.LastWriteTimeUtc;
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                file.Refresh();
                if (!file.Exists || file.Length != size || stream.Length != size || file.LastWriteTimeUtc != modified)
                    throw new IOException($"File changed during the scan: {file.FullName}");
                tracks.Add(new SourceTrack(Path.GetRelativePath(root.FullName, file.FullName), size,
                    modified, Convert.ToHexString(hash)));
            }
        }
        tracks.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new SourceLibrary(root.FullName, tracks);
    }
}
