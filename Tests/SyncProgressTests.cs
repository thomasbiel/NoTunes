using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncProgressTests
{
    [Test]
    public async Task Reports_All_Write_Phases_And_Only_Enables_Track_Diagnostics_When_Requested([Values] bool debug)
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        await using var error = new StringWriter();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        var path = SourceTestData.WriteSyncTrack(
            source.Root,
            "new.mp3",
            configure: file => file.Tag.Title = "New");

        var oldAudio = await File.ReadAllBytesAsync(path);
        oldAudio[^1] ^= 1;
        device.SetExistingAudio(oldAudio);
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, error).BuildServiceProvider();
        var engine = device.CreateEngine(
            services.GetRequiredService<SourceScanner>(),
            services.GetRequiredService<ISourceFileSystem>(),
            error);

        var result = await engine.RunAsync(new SyncOptions
        {
            Folder = source.Root,
            StateFolder = state.Root,
            Timeout = SimulatedSyncDevice.RequestTimeoutSeconds,
            Debug = debug
        }, CancellationToken.None);

        var diagnostics = error.ToString();
        foreach (var phase in new[]
        {
            "Scanning and hashing source", "Reading iPhone catalogue", "Comparing iPhone tracks",
            "Preparing source tracks", "Checking and preparing covers", "Transferring assets",
            "Verifying transferred tracks", "Removing obsolete iPhone tracks"
        })
        {
            Assert.That(diagnostics, Does.Contain($"{phase}: 100%"));
        }

        Assert.That(diagnostics.Contains("Compare: ", StringComparison.Ordinal), Is.EqualTo(debug));
        Assert.That(diagnostics.Contains("Transfer: ", StringComparison.Ordinal), Is.EqualTo(debug));
        Assert.That(result.Plan.Add.Single().RelativePath, Is.EqualTo(Path.GetFileName(path)));
        Assert.That(result.Plan.Delete, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Unchanged, Is.Zero);
    }

    [Test]
    public async Task Completes_Preparation_Progress_When_Invalid_Files_Are_Skipped([Values] bool artworkOnly)
    {
        using var source = new SyncWorkspace();
        using var workspace = new SyncWorkspace();
        await using var error = new StringWriter();
        if (artworkOnly)
        {
            SourceTestData.WriteCoveredTrack(source.Root, "invalid.mp3", coverBytes: [1, 2, 3, 4]);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(source.Root, "invalid.mp3"), "Invalid MP3");
        }

        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, error).BuildServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(source.Root, CancellationToken.None);
        var catalogue = CatalogueTestData.Create([]) with { ArtworkAvailable = true };
        var plan = new SyncPlan(
            "phone",
            catalogue.Anchor,
            library.Tracks,
            [],
            0);

        var skipped = new List<SkippedSourceFile>();
        var prepared = await SourcePreparation.PrepareAsync(
            library,
            plan,
            catalogue,
            services.GetRequiredService<ISourceFileSystem>(),
            workspace,
            CancellationToken.None,
            new SourcePreparationOptions { ArtworkOnly = artworkOnly, OnSkipped = skipped.Add, Error = error });

        Assert.That(prepared, Is.Empty);
        Assert.That(skipped, Has.Count.EqualTo(1));
        var phase = artworkOnly ? "Checking and preparing covers" : "Preparing source tracks";
        Assert.That(error.ToString(), Does.Contain($"{phase}: 100% (1/1)"));
    }
}
