using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class DeviceHashCacheIntegrationTests
{
    private SyncWorkspace source = null!;
    private SyncWorkspace state = null!;
    private SyncWorkspace cache = null!;
    private SimulatedSyncDevice device = null!;
    private StringWriter error = null!;
    private ServiceProvider services = null!;
    private SyncOptions options = null!;
    private bool wrongSize;

    [SetUp]
    public async Task SetUp()
    {
        this.source = new SyncWorkspace();
        this.state = new SyncWorkspace();
        this.cache = new SyncWorkspace();
        this.wrongSize = false;
        this.device = new SimulatedSyncDevice(keepExistingTrack: true)
        {
            ConfigureFileConnection = peer =>
            {
                peer.AllowPipelinedFileInfo = true;
                if (this.wrongSize)
                {
                    peer.FileInfoOverrides[SimulatedSyncCatalogue.ExistingTrackPath] = new()
                    {
                        [Apple.AfcKeys.FileSize] = "0"
                    };
                }
            }
        };
        var path = this.WriteTrack("song.mp3");
        this.device.SetExistingAudio(await File.ReadAllBytesAsync(path));
        this.error = new StringWriter();
        this.services = new ServiceCollection().AddNoTunes(TextWriter.Null, this.error).BuildServiceProvider();
        this.options = new SyncOptions
        {
            Folder = this.source.Root,
            StateFolder = this.state.Root,
            Timeout = SimulatedSyncDevice.RequestTimeoutSeconds,
            DryRun = true
        };
    }

    [TearDown]
    public async Task TearDown()
    {
        await this.services.DisposeAsync();
        this.error.Dispose();
        this.device.Dispose();
        this.cache.Dispose();
        this.state.Dispose();
        this.source.Dispose();
    }

    [Test]
    public async Task Repeated_Dry_Runs_Reuse_Hashes_Unless_Full_Readback_Is_Requested([Values] bool fullReadback)
    {
        var first = await this.RunAsync();
        var connections = this.device.FileConnections.Count;
        this.options.FullReadback = fullReadback;
        var second = await this.RunAsync();
        var reads = this.device.FileConnections.Skip(connections).Sum(peer =>
            peer.ReadPaths.Count(read => read == SimulatedSyncCatalogue.ExistingTrackPath));

        Assert.That(first.Plan.Unchanged, Is.EqualTo(1));
        Assert.That(second.Plan.Unchanged, Is.EqualTo(first.Plan.Unchanged));
        Assert.That(second.Plan.Add, Is.Empty);
        Assert.That(second.Plan.Delete, Is.Empty);
        Assert.That(reads > 0, Is.EqualTo(fullReadback));
        Assert.That(this.device.StartedSessions, Is.Zero);
        Assert.That(this.device.FileConnections.SelectMany(peer => peer.WritePackets), Is.Empty);
        Assert.That(Directory.GetFiles(this.cache.Root, "*.json"), Has.Length.EqualTo(1));
        Assert.That(Directory.EnumerateFileSystemEntries(this.state.Root), Is.Empty);
    }

    [Test]
    public async Task Cache_Hits_Do_Not_Skip_Readback_Of_New_Audio_Or_Separate_Covers()
    {
        await this.RunAsync();
        this.WriteTrack("added.mp3", artwork: true);
        this.options.DryRun = false;
        var result = await this.RunAsync();
        var after = await this.device.ReadCatalogueAsync();
        var added = after.Tracks.Single(track => track.Id != SimulatedSyncCatalogue.ExistingTrackId);
        var artwork = Apple.SyncPaths.ArtworkOriginals + after.TrackArtwork[added.Id].Path;
        var paths = this.device.FileConnections.SelectMany(peer => peer.ReadPaths).ToArray();
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(paths, Does.Contain(added.Path));
        Assert.That(paths, Does.Contain(artwork));
        Assert.That(this.error.ToString(), Does.Contain("Device hashes: 1 cached, 0 read from iPhone."));
    }

    [Test]
    public async Task A_Failed_Comparison_Does_Not_Replace_The_Previous_Cache_Or_Write_To_The_Device()
    {
        await this.RunAsync();
        var path = Directory.GetFiles(this.cache.Root, "*.json").Single();
        var before = await File.ReadAllBytesAsync(path);
        this.wrongSize = true;
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync());
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
        Assert.That(this.device.StartedSessions, Is.Zero);
        Assert.That(this.device.FileConnections.SelectMany(peer => peer.WritePackets), Is.Empty);
    }

    private Task<SyncResult> RunAsync()
    {
        var engine = this.device.CreateEngine(
            this.services.GetRequiredService<SourceScanner>(),
            this.services.GetRequiredService<ISourceFileSystem>(),
            this.error,
            this.cache.Root);
        return engine.RunAsync(this.options, CancellationToken.None);
    }

    private string WriteTrack(string name, bool artwork = false)
    {
        return SourceTestData.WriteTrack(
            this.source.Root,
            name,
            "Album",
            "Artist",
            file =>
            {
                file.Tag.Title = name;
                file.Tag.Composers = ["Composer"];
                file.Tag.Genres = ["Genre"];
                if (artwork) file.Tag.Pictures = [FeatureTestData.FrontCover()];
            });
    }
}
