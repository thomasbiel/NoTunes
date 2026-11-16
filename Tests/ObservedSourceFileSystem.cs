using System.Runtime.Versioning;
using NoTunes.Platform;
using NoTunes.Platform.Linux;

namespace NoTunes.Tests;

[SupportedOSPlatform("linux")]
internal sealed class ObservedSourceFileSystem(Action observe) : ISourceFileSystem
{
    public ISourceSession OpenSource(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        return new ObservedSourceSession(LinuxSourceSession.Acquire(directory, cancellationToken), observe);
    }
}
