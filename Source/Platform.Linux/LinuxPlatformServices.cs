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
