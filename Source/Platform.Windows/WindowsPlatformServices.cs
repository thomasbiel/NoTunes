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
