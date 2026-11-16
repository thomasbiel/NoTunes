using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class ObservedSourceSession(ISourceSession inner, Action observe) : ISourceSession
{
    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        observe();
        return inner.EnumerateDirectory(directory);
    }

    public void AddDirectory(DirectoryInfo directory) => inner.AddDirectory(directory);

    public void ReleaseDirectory(DirectoryInfo directory) => inner.ReleaseDirectory(directory);

    public FileStream OpenRead(FileInfo file) => inner.OpenRead(file);

    public void Dispose() => inner.Dispose();
}
