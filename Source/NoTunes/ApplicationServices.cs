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
        public IServiceCollection AddNoTunes(TextWriter output, TextWriter error, TextReader? input = null)
        {
            if (OperatingSystem.IsWindows())
                services.AddWindowsPlatform();
            else if (OperatingSystem.IsLinux())
                services.AddLinuxPlatform();
            else
                throw new PlatformNotSupportedException("NoTunes currently provides platform services for Windows and Linux.");
        
            return services.AddApplication(output, error, input);
        }

        internal IServiceCollection AddApplication(TextWriter output, TextWriter error, TextReader? input = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);
            services.AddSingleton(new GroupResolutionPrompt(input ?? TextReader.Null, error));
            services.AddSingleton(_ => new SourceHashCache(
                Path.Combine(DeviceState.Root(null), SourceHashCache.DirectoryName), error));
            services.AddTransient<SourceScanner>();
            services.AddTransient(provider => new LibraryInitializer(
                provider.GetRequiredService<Platform.IUsbMuxTransport>(), error));
            services.AddTransient(provider => new SyncEngine(
                provider.GetRequiredService<SourceScanner>(),
                provider.GetRequiredService<Platform.ISourceFileSystem>(),
                provider.GetRequiredService<Platform.IUsbMuxTransport>(),
                error)
            {
                PromptForGroupName = provider.GetRequiredService<GroupResolutionPrompt>().ReadAsync
            });
        
            services.AddTransient(provider => new CommandContext(
                provider.GetRequiredService<SourceScanner>(),
                provider.GetRequiredService<Platform.IUsbMuxTransport>(),
                provider.GetRequiredService<SyncEngine>(),
                provider.GetRequiredService<LibraryInitializer>(),
                output));
        
            return services;
        }
    }
}
