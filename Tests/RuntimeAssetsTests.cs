using System.Text.Json;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class RuntimeAssetsTests
{
    private static readonly string[] SupportedNativeRuntimes =
        ["win-x86", "win-x64", "win-arm64", "linux-x64", "linux-arm64"];

    [Test]
    public void Output_Contains_Only_The_Supported_Runtime_Variants()
    {
        var output = AppContext.BaseDirectory;
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "NoTunes.Tests.deps.json")));
        var runtimeTarget = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        var runtimesDirectory = Path.Combine(output, "runtimes");
        if (runtimeTarget.Contains('/'))
        {
            Assert.That(Directory.Exists(runtimesDirectory), Is.False,
                "A runtime-specific build should copy its selected native library beside the assemblies.");

            return;
        }

        Assert.That(Directory.Exists(runtimesDirectory), Is.True);
        var nativeRuntimes = Directory.EnumerateDirectories(runtimesDirectory)
            .Where(directory => Directory.Exists(Path.Combine(directory, "native")) &&
                                Directory.EnumerateFiles(Path.Combine(directory, "native")).Any())
            .Select(Path.GetFileName)
            .ToArray();

        Assert.That(nativeRuntimes, Is.EquivalentTo(SupportedNativeRuntimes));
        var runtimeDirectories = Directory.EnumerateDirectories(runtimesDirectory)
            .Where(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
            .Select(Path.GetFileName)
            .ToArray();

        Assert.That(runtimeDirectories, Is.SubsetOf(SupportedNativeRuntimes.Concat(
            ["win", "linux", "unix", "unix-x64", "unix-arm64", "any", "base"])));

        Assert.That(File.Exists(Path.Combine(
            runtimesDirectory,
            "win",
            "lib",
            "net10.0",
            "System.Diagnostics.EventLog.dll")), Is.True);
    }
}
