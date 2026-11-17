using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SyncRecoveryTests
{
    [Test]
    public async Task Persists_Metadata_And_Audio_For_Recovery_After_The_Workspace_Is_Gone()
    {
        using var workspace = new SyncWorkspace();
        using var stateRoot = new SyncWorkspace();
        using var fixture = new CatalogueFixture();
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        var track = FeatureTestData.Covered() with { LocalPath = Path.Combine(workspace.Root, "copy.mp3") };
        File.Copy(SyncTestData.FixturePath, track.LocalPath);
        using (var journal = new SyncJournal(stateRoot.Root, "phone"))
        {
            await journal.StartBatchAsync(
                new LibraryIdentity("id", "binding"),
                before,
                SyncBatch.Create(before, [track], []),
                [1, 2],
                CancellationToken.None);
        }

        File.Delete(track.LocalPath);
        using var resumed = new SyncJournal(stateRoot.Root, "phone", allowPending: true);
        var pending = resumed.ReadPending();
        Assert.DoesNotThrow(() => SyncEngine.RequireUnchanged(before, pending.Before.Restore()));
        var recovered = pending.Additions.Single().Restore(resumed.Assets(pending));
        Assert.That(File.ReadAllBytes(recovered.LocalPath), Is.EqualTo(File.ReadAllBytes(SyncTestData.FixturePath)));
        Assert.That(recovered.Metadata.Artwork!.Bytes, Is.EqualTo(track.Metadata.Artwork!.Bytes));
        Assert.DoesNotThrow(() => SyncRecovery.Assess(pending, before, [recovered]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Distinguishes_Completed_And_Unfilled_Insertions_Without_Replaying_Them(bool incomplete)
    {
        var before = SyncPlannerTests.Catalogue([]);
        var addition = SyncTestData.Track();
        var actual = FeatureTestData.Stored(addition);
        if (incomplete) actual = actual with { Path = null, Size = 0 };

        var after = before with
        {
            Anchor = before.Anchor + 1,
            Tracks = [actual],
            Albums = new Dictionary<string, long>
            {
                [CatalogueReader.GroupKey(addition.Metadata.Album, addition.Metadata.AlbumArtist)] = addition.AlbumId
            },
            Artists = new Dictionary<string, long> { [addition.Metadata.AlbumArtist] = addition.ArtistId }
        };
        var pending = new PendingSync(
            2,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [JournalTrack.Capture(addition)],
            [],
            SyncOperations.Serialize(before, [addition], []),
            [1, 2]);
        var repair = SyncRecovery.Assess(pending, after, [addition]);
        Assert.That(repair.Count, Is.EqualTo(incomplete ? 1 : 0));
        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, after with { Anchor = after.Anchor + 1 }, [addition]));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Cleans_Only_The_Recorded_Staging_Prefix_And_Refuses_Foreign_Data(bool owned)
    {
        var files = new AfcPeer { AllowWrites = true };
        files.Files[SyncPaths.Metadata] = owned ? [1, 2] : [2, 1];
        await using var afc = new AfcClient(files, TimeSpan.FromSeconds(1));
        if (owned)
        {
            await DeviceStaging.RemoveMatchingAsync(afc, SyncPaths.Metadata, new byte[] { 1, 2, 3 }, CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => DeviceStaging.RemoveMatchingAsync(
                afc,
                SyncPaths.Metadata,
                new byte[] { 1, 2, 3 },
                CancellationToken.None));
        }

        Assert.That(files.Files.ContainsKey(SyncPaths.Metadata), Is.EqualTo(!owned));
    }

    [Test]
    public void Reconciles_Partially_Applied_Deletions_Without_Replaying_The_Original_Batch()
    {
        var before = SyncPlannerTests.Catalogue([
            new DeviceTrack(101, "First", "first.mp3", 1),
            new DeviceTrack(102, "Second", "second.mp3", 1)]);
        var pending = new PendingSync(
            2,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [],
            before.Tracks.ToArray(),
            SyncOperations.Serialize(before, [], before.Tracks),
            [1, 2]);
        var after = before with { Anchor = before.Anchor + 1, Tracks = [before.Tracks[1]] };
        Assert.That(SyncRecovery.Assess(pending, after, []), Is.Empty);
        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending,
            after with { Tracks = [before.Tracks[1] with { Title = "Foreign edit" }] }, []));
    }

    [Test]
    public void Reconciles_An_Insert_Whose_Revision_Advanced_Without_Creating_The_Row()
    {
        var before = SyncPlannerTests.Catalogue([]);
        var addition = SyncTestData.Track();
        var pending = new PendingSync(
            2,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [JournalTrack.Capture(addition)],
            [],
            SyncOperations.Serialize(before, [addition], []),
            [1, 2]);
        Assert.That(SyncRecovery.Assess(pending, before with { Anchor = before.Anchor + 1 }, [addition]), Is.Empty);
    }
}
