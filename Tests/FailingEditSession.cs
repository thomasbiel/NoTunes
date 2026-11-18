using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class FailingEditSession(Action onFirstWrite) : ISourceEditSession
{
    public bool Disposed { get; private set; }

    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        throw new AssertionException("Removing one cover must not enumerate the library.");
    }

    public void AddDirectory(DirectoryInfo directory)
    {
    }

    public void ReleaseDirectory(DirectoryInfo directory)
    {
    }

    public FileStream OpenRead(FileInfo file)
    {
        return File.OpenRead(file.FullName);
    }

    public FileStream OpenEdit(FileInfo file)
    {
        return new FailOnceEditStream(file.FullName, onFirstWrite);
    }

    public void Dispose()
    {
        this.Disposed = true;
    }
}
