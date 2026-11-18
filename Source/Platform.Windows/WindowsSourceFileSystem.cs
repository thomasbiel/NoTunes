namespace NoTunes.Platform.Windows;

public sealed class WindowsSourceFileSystem : ISourceFileSystem
{
    public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Windows source provider requires Windows.");

        return WindowsSourceSession.Acquire(root, cancellationToken);
    }
}
