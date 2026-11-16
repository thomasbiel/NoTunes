using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class MutatingSourceFileSystem(string file, Action mutate) : ISourceFileSystem
{
    public MutatingSourceSession Session { get; } = new(file, mutate);

    public ISourceSession OpenSource(DirectoryInfo directory, CancellationToken cancellationToken) => this.Session;
}
