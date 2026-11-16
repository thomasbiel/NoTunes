using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform.Linux;
using NoTunes.Platform.Windows;

namespace NoTunes;

public static class ApplicationServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddNoTunes(TextWriter output, TextWriter error)
        {
            if (OperatingSystem.IsWindows())
                services.AddWindowsPlatform();
            else if (OperatingSystem.IsLinux())
                services.AddLinuxPlatform();
            else
                throw new PlatformNotSupportedException("NoTunes currently provides platform services for Windows and Linux.");
        
            return services.AddApplication(output, error);
        }

        internal IServiceCollection AddApplication(TextWriter output, TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);
            services.AddTransient<SourceScanner>();
            services.AddTransient(provider => new SyncEngine(
                provider.GetRequiredService<SourceScanner>(),
                provider.GetRequiredService<NoTunes.Platform.ISourceFileSystem>(),
                provider.GetRequiredService<NoTunes.Platform.IUsbMuxTransport>(),
                error));
        
            services.AddTransient(provider => new Commands(
                provider.GetRequiredService<SourceScanner>(),
                provider.GetRequiredService<NoTunes.Platform.IUsbMuxTransport>(),
                provider.GetRequiredService<SyncEngine>(),
                output));
        
            return services;
        }
    }
}
