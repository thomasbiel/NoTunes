using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CatalogueStabilityTests
{
    [Test]
    public async Task Retries_A_Transient_Catalogue_Change_Without_Starting_A_Sync_Operation()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        var changed = false;
        using var device = new SimulatedSyncDevice
        {
            ConfigureFileConnection = peer =>
            {
                peer.OnFileClosed = path =>
                {
                    if (!changed && StringComparer.Ordinal.Equals(path, CatalogueSnapshot.DatabasePath))
                    {
                        changed = true;
                        peer.Files[CatalogueSnapshot.DatabasePath + SyncPaths.WalSuffix] = [];
                    }
                };
            }
        };
        File.Copy(SyncTestData.FixturePath, Path.Combine(source.Root, "new.mp3"));
        var result = await device.RunAsync(source.Root, state.Root, dryRun: true);
        Assert.That(changed, Is.True);
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(device.StartedSessions, Is.Zero);
        Assert.That(device.Batches, Is.Empty);
        Assert.That(Directory.EnumerateFileSystemEntries(state.Root), Is.Empty);
    }

    [Test]
    public async Task Still_Rejects_Persistent_Byte_Drift_After_Bounded_Capture_Retries()
    {
        const int changingOffset = 100;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var peer = new AfcPeer();
        var database = new byte[128];
        "SQLite format 3\0"u8.CopyTo(database);
        peer.Files[CatalogueSnapshot.DatabasePath] = database;
        peer.OnClose = () => peer.Files[CatalogueSnapshot.DatabasePath][changingOffset]++;
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => CatalogueSnapshot.CaptureWithRetryAsync(afc, deadline.Token));
        Assert.That(peer.Opens, Is.LessThanOrEqualTo(10));
        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Preserves_Caller_Cancellation_Without_Retrying_The_Connection()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var peer = new AfcPeer();
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.CatchAsync<OperationCanceledException>(() => CatalogueSnapshot.CaptureWithRetryAsync(
            afc, cancellation.Token));
        Assert.That(peer.Opens, Is.Zero);
    }
}
