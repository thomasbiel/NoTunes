namespace NoTunes.Platform;

public interface ISourceSession : IDisposable
{
    IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory);

    void AddDirectory(DirectoryInfo directory);

    // Release traversal resources; providers may retain guards needed for whole-scan safety.
    void ReleaseDirectory(DirectoryInfo directory);

    FileStream OpenRead(FileInfo file);
}
