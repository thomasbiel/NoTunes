using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class FailingEditFileSystem(Action onFirstWrite) : ISourceFileSystem
{
    public FailingEditSession Session { get; } = new(onFirstWrite);

    public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken)
    {
        return this.Session;
    }
}
