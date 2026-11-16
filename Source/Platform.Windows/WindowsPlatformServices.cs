using Microsoft.Extensions.DependencyInjection;

namespace NoTunes.Platform.Windows;

public static class WindowsPlatformServices
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IUsbMuxTransport>(_ => new WindowsUsbMuxTransport());
        services.AddSingleton<ISourceFileSystem, WindowsSourceFileSystem>();
        return services;
    }
}

public sealed class WindowsSourceFileSystem : ISourceFileSystem
{
    public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Windows source provider requires Windows.");
        return WindowsSourceSession.Acquire(root, cancellationToken);
    }
}
