namespace NoTunes.Platform;

public interface ISourceFileSystem
{
    ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken);
}

public interface ISourceSession : IDisposable
{
    IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory);
    void AddDirectory(DirectoryInfo directory);
    FileStream OpenRead(FileInfo file);
}

public sealed record SourceEntry(string Name, FileAttributes Attributes);
