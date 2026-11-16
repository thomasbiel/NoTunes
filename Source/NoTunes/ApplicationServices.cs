using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform.Linux;
using NoTunes.Platform.Windows;

namespace NoTunes;

public static class ApplicationServices
{
    public static IServiceCollection AddNoTunes(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
            services.AddWindowsPlatform();
        else if (OperatingSystem.IsLinux())
            services.AddLinuxPlatform();
        else
            throw new PlatformNotSupportedException("NoTunes currently provides platform services for Windows and Linux.");
        services.AddTransient<SourceScanner>();
        services.AddTransient<Commands>();
        return services;
    }
}
