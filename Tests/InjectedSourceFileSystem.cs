using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class InjectedSourceFileSystem(string actual) : ISourceFileSystem
{
    public InjectedSourceSession Session { get; } = new(actual);

    public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken) => this.Session;
}
