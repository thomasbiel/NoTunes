using NoTunes.Platform;

namespace NoTunes.Tests;

internal sealed class BudgetSourceFileSystem : ISourceFileSystem
{
    public bool FailRead { get; init; }

    public Action? BeforeEnumeration { get; init; }

    public Action<CancellationToken>? BeforeRead { get; init; }

    public BudgetSourceSession Session { get; private set; } = null!;

    public ISourceSession OpenSource(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        return this.Session = new BudgetSourceSession(
            directory,
            this.FailRead,
            this.BeforeEnumeration,
            () => this.BeforeRead?.Invoke(cancellationToken));
    }
}
