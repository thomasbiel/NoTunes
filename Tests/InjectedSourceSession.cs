using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class InjectedSourceSession(string actual) : ISourceSession
{
    public bool Disposed { get; private set; }

    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        return [new("injected.mp3", FileAttributes.Normal)];
    }

    public void AddDirectory(DirectoryInfo directory) => throw new NotSupportedException();

    public void ReleaseDirectory(DirectoryInfo directory)
    {
    }

    public FileStream OpenRead(FileInfo file)
    {
        return new(
            actual,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
    }

    public void Dispose() => this.Disposed = true;
}
