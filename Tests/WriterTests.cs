using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class WriterTests
{
    [Test]
    public async Task Writes_Plain_Scan_Output_To_The_Provided_Writer()
    {
        using var source = new SyncWorkspace();
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        await using var services = new ServiceCollection().AddNoTunes(output, error).BuildServiceProvider();
        var result = await services.GetRequiredService<Commands>().ScanAsync(new ScanOptions { Folder = source.Root }, CancellationToken.None);
        Assert.That(result, Is.Zero);
        Assert.That(output.ToString(), Does.Contain("0 MP3 file(s)"));
        Assert.That(error.ToString(), Is.Empty);
    }

    [Test]
    public async Task Writes_Sync_Diagnostics_Only_To_The_Provided_Error_Writer()
    {
        using var workspace = new SyncWorkspace();
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        await using var services = new ServiceCollection().AddNoTunes(output, error).BuildServiceProvider();
        var missing = Path.Combine(workspace.Root, "missing");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => services.GetRequiredService<SyncEngine>().RunAsync(
            new SyncOptions { Folder = missing }, CancellationToken.None));
        Assert.That(error.ToString(), Does.Contain("Scanning and hashing source music"));
        Assert.That(output.ToString(), Is.Empty);
    }

    [Test]
    public async Task Writes_Scan_Json_To_The_Provided_Output_Writer()
    {
        using var source = new Music.SyncWorkspace();
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        await using var services = new ServiceCollection().AddNoTunes(output, error).BuildServiceProvider();
        var result = await services.GetRequiredService<Commands>().ScanAsync(new ScanOptions { Folder = source.Root, Json = true }, CancellationToken.None);
        Assert.That(result, Is.Zero);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.That(json.RootElement.GetProperty("Tracks").GetArrayLength(), Is.Zero);
        Assert.That(error.ToString(), Is.Empty);
    }
}
