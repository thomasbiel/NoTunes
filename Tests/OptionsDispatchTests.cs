using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class OptionsDispatchTests
{
    [Test]
    public async Task Executes_A_Parsed_Scan_Through_The_Common_Options_Base()
    {
        using var source = new SyncWorkspace();
        await using var output = new StringWriter();
        await using var services = new ServiceCollection().AddNoTunes(output, TextWriter.Null).BuildServiceProvider();
        using var parser = new CommandLine.Parser(settings => settings.HelpWriter = null);
        var result = parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions, InitOptions>(
            ["scan", source.Root]);
        var code = await result.MapResult(
            options => ((CommandOptions)options).Execute(services.GetRequiredService<CommandContext>(), CancellationToken.None),
            _ => Task.FromResult(ExitCodes.InvalidArguments));
        Assert.That(code, Is.Zero);
        Assert.That(output.ToString(), Does.Contain("0 MP3 file(s)"));
    }

    [TestCase("devices")]
    [TestCase("probe")]
    [TestCase("sync")]
    [TestCase("init")]
    public async Task Validates_Device_Options_Through_The_Common_Dispatch(string verb)
    {
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null)
            .BuildServiceProvider();
        using var parser = new CommandLine.Parser(settings => settings.HelpWriter = null);
        var result = parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions, InitOptions>(
            [verb, "--timeout", "0"]);
        await Assert.ThrowsAsync<InvalidArgumentsException>(() => result.MapResult(
            options => ((CommandOptions)options).Execute(services.GetRequiredService<CommandContext>(), CancellationToken.None),
            _ => Task.FromResult(ExitCodes.InvalidArguments)));
    }
}
