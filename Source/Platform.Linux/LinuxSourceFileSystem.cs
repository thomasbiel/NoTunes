namespace NoTunes.Platform.Linux;

public sealed class LinuxSourceFileSystem : ISourceFileSystem
{
    public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The Linux source provider requires Linux.");

        return LinuxSourceSession.Acquire(root, cancellationToken);
    }
}
