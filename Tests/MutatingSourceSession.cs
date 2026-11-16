using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class MutatingSourceSession(string file, Action mutate) : ISourceSession
{
    public MutatingSourceStream? OpenedStream { get; private set; }

    public bool Disposed { get; private set; }

    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        return [new(Path.GetFileName(file), FileAttributes.Normal)];
    }

    public void AddDirectory(DirectoryInfo directory) => throw new NotSupportedException();

    public void ReleaseDirectory(DirectoryInfo directory)
    {
    }

    public FileStream OpenRead(FileInfo info) => this.OpenedStream = new MutatingSourceStream(file, mutate);

    public void Dispose() => this.Disposed = true;
}
