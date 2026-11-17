using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class WriterTests
{
    [Test]
    public async Task Reports_Skipped_Files_And_Deferred_Deletions_As_A_Partial_Result(
        [Values] bool dryRun,
        [Values] bool json)
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        await using var services = new ServiceCollection().AddNoTunes(output, error).BuildServiceProvider();
        SourceTestData.WriteTrack(
            source.Root,
            "valid.mp3",
            "Album",
            "Artist",
            file =>
            {
                file.Tag.Composers = ["Composer"];
                file.Tag.Genres = ["Genre"];
            });
        const string invalidName = "invalid.mp3";
        await File.WriteAllTextAsync(Path.Combine(source.Root, invalidName), "Not an MP3");
        var engine = device.CreateEngine(
            services.GetRequiredService<SourceScanner>(),
            services.GetRequiredService<ISourceFileSystem>(),
            error);
        var context = services.GetRequiredService<CommandContext>() with { SyncEngine = engine };
        var options = new SyncOptions
        {
            Folder = source.Root,
            StateFolder = state.Root,
            DryRun = dryRun,
            Json = json,
            Timeout = SimulatedSyncDevice.RequestTimeoutSeconds
        };

        var code = await options.Execute(context, CancellationToken.None);

        Assert.That(code, Is.EqualTo(ExitCodes.PartialSuccess));
        Assert.That(error.ToString(), Does.Contain($"Skipped source file: {invalidName}:"));
        Assert.That(error.ToString(), Does.Contain("Deferred 1 deletion(s)"));
        if (json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
            var result = document.RootElement;
            var plan = result.GetProperty("Plan");
            Assert.That(plan.GetProperty("Add").GetArrayLength(), Is.EqualTo(1));
            Assert.That(plan.GetProperty("Delete").GetArrayLength(), Is.Zero);
            Assert.That(plan.GetProperty("Skipped")[0].GetProperty("RelativePath").GetString(), Is.EqualTo(invalidName));
            Assert.That(plan.GetProperty("Skipped")[0].GetProperty("Reason").GetString(), Is.Not.Empty);
            Assert.That(plan.GetProperty("DeferredDeletions").GetArrayLength(), Is.EqualTo(1));
            Assert.That(result.GetProperty("VerifiedBatches").GetInt32(), Is.EqualTo(dryRun ? 0 : 1));
        }
        else
        {
            Assert.That(output.ToString(), Does.Contain("1 added, 0 removed"));
            Assert.That(output.ToString(), Does.Contain("1 file(s) skipped, 1 deletion(s) deferred"));
        }

        Assert.That(device.Files.ContainsKey(SimulatedSyncCatalogue.ExistingTrackPath), Is.True);
    }

    [Test]
    public async Task Writes_Scan_Output_To_The_Provided_Writer([Values] bool json)
    {
        using var source = new SyncWorkspace();
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        await using var services = new ServiceCollection().AddNoTunes(output, error).BuildServiceProvider();
        var result = await new ScanOptions { Folder = source.Root, Json = json }
            .Execute(services.GetRequiredService<CommandContext>(), CancellationToken.None);
        Assert.That(result, Is.Zero);
        if (json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.That(document.RootElement.GetProperty("Tracks").GetArrayLength(), Is.Zero);
        }
        else
        {
            Assert.That(output.ToString(), Does.Contain("0 MP3 file(s)"));
        }

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
}
