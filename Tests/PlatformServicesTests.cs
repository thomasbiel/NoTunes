using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform;
using NoTunes.Platform.Linux;
using NoTunes.Platform.Windows;
using NUnit.Framework;
using System.Runtime.InteropServices;

namespace NoTunes.Tests;

public sealed class PlatformServicesTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void RegistersPlatformServicesWithoutOpeningNativeResources(bool windows)
    {
        var registrations = new ServiceCollection();
        if (windows) registrations.AddWindowsPlatform();
        else registrations.AddLinuxPlatform();
        registrations.AddTransient<SourceScanner>();
        registrations.AddTransient<Commands>();
        using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.That(services.GetRequiredService<IUsbMuxTransport>(),
            windows ? Is.TypeOf<WindowsUsbMuxTransport>() : Is.TypeOf<LinuxUsbMuxTransport>());
        Assert.That(services.GetRequiredService<ISourceFileSystem>(),
            windows ? Is.TypeOf<WindowsSourceFileSystem>() : Is.TypeOf<LinuxSourceFileSystem>());
        Assert.That(services.GetRequiredService<Commands>(), Is.Not.Null);
    }

    [Test]
    public void ApplicationRegistersTheCurrentPlatform()
    {
        using var services = new ServiceCollection().AddNoTunes().BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.That(services.GetRequiredService<IUsbMuxTransport>(), OperatingSystem.IsWindows()
            ? Is.TypeOf<WindowsUsbMuxTransport>() : Is.TypeOf<LinuxUsbMuxTransport>());
        Assert.That(services.GetRequiredService<Commands>(), Is.Not.Null);
    }

    [Test]
    public void CoreReferencesOnlyPlatformContracts()
    {
        var references = typeof(SourceScanner).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        Assert.That(references, Does.Contain("NoTunes.Platform.Abstractions"));
        Assert.That(references, Does.Not.Contain("NoTunes.Platform.Windows"));
        Assert.That(references, Does.Not.Contain("NoTunes.Platform.Linux"));
        Assert.That(typeof(SourceScanner).Assembly, Is.Not.SameAs(typeof(ApplicationServices).Assembly));
    }

    [TestCase(Architecture.X64, 65536, 131072)]
    [TestCase(Architecture.Arm64, 16384, 32768)]
    public void LinuxOpenFlagsMatchTheArchitecture(Architecture architecture, int directory, int noFollow)
    {
        var flags = LinuxOpenFlags.ForArchitecture(architecture);
        Assert.That(flags.Directory, Is.EqualTo(directory));
        Assert.That(flags.NoFollow, Is.EqualTo(noFollow));
    }

    [Test]
    public void LinuxRejectsUnsupportedArchitecturesBeforeOpeningAnything() =>
        Assert.That(() => LinuxOpenFlags.ForArchitecture(Architecture.X86), Throws.TypeOf<PlatformNotSupportedException>());

    [Test]
    public async Task ScannerUsesTheInjectedFileSystemAndDisposesTheSession()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NoTunes-injection-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var actual = Path.Combine(root, "actual.bin");
        try
        {
            await File.WriteAllTextAsync(actual, "abc");
            var fileSystem = new StubFileSystem(actual);
            using var services = new ServiceCollection().AddSingleton<ISourceFileSystem>(fileSystem)
                .AddTransient<SourceScanner>().BuildServiceProvider();
            var result = await services.GetRequiredService<SourceScanner>().ScanAsync(root, CancellationToken.None);
            Assert.That(result.Tracks.Single().RelativePath, Is.EqualTo("injected.mp3"));
            Assert.That(result.TotalBytes, Is.EqualTo(3));
            Assert.That(fileSystem.Session.Disposed, Is.True);
        }
        finally
        {
            Assert.That(root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), Is.True);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubFileSystem(string actual) : ISourceFileSystem
    {
        public StubSession Session { get; } = new(actual);
        public ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken) => Session;
    }

    private sealed class StubSession(string actual) : ISourceSession
    {
        public bool Disposed { get; private set; }
        public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory) => [new("injected.mp3", FileAttributes.Normal)];
        public void AddDirectory(DirectoryInfo directory) => throw new NotSupportedException();
        public FileStream OpenRead(FileInfo file) => new(actual, FileMode.Open, FileAccess.Read, FileShare.Read);
        public void Dispose() => Disposed = true;
    }
}
