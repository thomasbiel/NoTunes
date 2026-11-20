using NoTunes.Platform;

namespace NoTunes.Music;

public sealed class SourceScanner(
    ISourceFileSystem fileSystem,
    SourceHashCache? hashCache = null,
    TextWriter? error = null)
{
    private const string Mp3Extension = ".mp3";

    public Task<SourceLibrary> ScanAsync(string folder, CancellationToken cancellationToken)
    {
        return this.ScanAsync(GetSourceDirectory(folder), cancellationToken);
    }

    internal Task<SourceLibrary> ScanInBackgroundAsync(string folder, CancellationToken cancellationToken)
    {
        var root = GetSourceDirectory(folder);
        return Task.Run(() => this.ScanAsync(root, cancellationToken), cancellationToken);
    }

    private async Task<SourceLibrary> ScanAsync(DirectoryInfo root, CancellationToken cancellationToken)
    {
        using var paths = fileSystem.OpenSource(root, cancellationToken);
        var cached = hashCache?.Load(root.FullName);
        if (error is not null) await error.WriteLineAsync("Discovering source MP3 files...");

        var inventory = EnumerateFiles(paths, root, cancellationToken).ToHashSet(StringComparer.Ordinal);
        var progress = new OperationProgress(error ?? TextWriter.Null, "Scanning and hashing source", inventory.Count);
        var tracks = new List<SourceTrack>();
        foreach (var fullName in EnumerateFiles(paths, root, cancellationToken))
        {
            if (!inventory.Remove(fullName)) throw new IOException("Source inventory changed during the scan.");

            tracks.Add(await ReadTrackAsync(
                paths,
                root,
                fullName,
                cached,
                cancellationToken));

            progress.Advance();
        }

        if (inventory.Count != 0) throw new IOException("Source inventory changed during the scan.");

        tracks.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        cancellationToken.ThrowIfCancellationRequested();
        var library = new SourceLibrary(root.FullName, tracks);
        hashCache?.Save(library);
        return library;
    }

    private static IEnumerable<string> EnumerateFiles(
        ISourceSession paths,
        DirectoryInfo root,
        CancellationToken cancellationToken)
    {
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
                    if (current.Directory != root) paths.ReleaseDirectory(current.Directory);

                    continue;
                }

                var directory = current.Directory;
                var entry = current.Entries.Current;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException($"Symbolic link or junction in source folder: {Path.Combine(directory.FullName, entry.Name)}. " + "The scan is aborted to prevent an incomplete inventory from causing deletions.");
                }

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

                yield return fullName;
            }
        }
        finally
        {
            foreach (var directory in directories)
            {
                directory.Entries.Dispose();
            }
        }
    }

    private static DirectoryInfo GetSourceDirectory(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
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
        {
            throw new IOException($"The source path is not a directory: {root.FullName}");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The source folder must not be a symbolic link or directory junction.");
        }

        return root;
    }

    private static async Task<SourceTrack> ReadTrackAsync(
        ISourceSession paths,
        DirectoryInfo root,
        string fullName,
        IReadOnlyDictionary<string, SourceTrack>? cached,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = paths.OpenRead(new FileInfo(fullName));
        var size = stream.Length;
        var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        var relativePath = Path.GetRelativePath(root.FullName, fullName);
        string hash;
        if (cached is not null && cached.TryGetValue(relativePath, out var previous)
            && previous.Size == size && previous.LastWriteTimeUtc == modified)
        {
            hash = previous.Hash;
        }
        else
        {
            hash = await ContentHash.ComputeAsync(stream, cancellationToken);
        }

        if (stream.Length != size || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != modified)
        {
            throw new IOException($"File changed during the scan: {fullName}");
        }

        return new SourceTrack(
            relativePath,
            size,
            modified,
            hash);
    }
}
