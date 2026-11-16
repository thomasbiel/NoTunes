using Microsoft.Extensions.DependencyInjection;

namespace NoTunes.Platform.Linux;

public static class LinuxPlatformServices
{
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IUsbMuxTransport>(_ => new LinuxUsbMuxTransport());
        services.AddSingleton<ISourceFileSystem, LinuxSourceFileSystem>();
        return services;
    }
}

public sealed class LinuxSourceFileSystem : ISourceFileSystem
{
    public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The Linux source provider requires Linux.");
        return LinuxSourceSession.Acquire(root, cancellationToken);
    }
}
