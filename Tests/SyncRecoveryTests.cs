using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncRecoveryTests
{
    [Test]
    public void Retries_Only_The_Incomplete_Deletions_Owned_By_The_Pending_Operation([Values] bool revisionAdvanced)
    {
        var owned = new DeviceTrack(
            2001,
            "Owned interrupted insertion",
            null,
            0);

        var unrelated = new DeviceTrack(
            2002,
            "Unrelated interrupted insertion",
            null,
            0);

        var before = CatalogueTestData.Create([owned, unrelated]);
        var pending = new PendingSync(
            2,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [],
            [owned],
            SyncOperations.Serialize(before, [], [owned]),
            [1, 2]);

        var actual = before with { Anchor = revisionAdvanced ? before.Anchor + 1 : before.Anchor };
        Assert.That(SyncRecovery.Assess(pending, actual, []).Select(track => track.Id), Is.EqualTo([owned.Id]));
        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending,
            actual with { Tracks = [owned, unrelated with { Title = "Foreign change" }] }, []));
    }

    [Test]
    public Task Retains_Unapplied_Compensating_Deletions_Across_Journal_Replacements()
    {
        return AssertCompensatingDeletionsAsync(0, revisionAdvanced: false);
    }

    [Test]
    public Task Retains_Unfinished_Compensating_Deletions_Across_Journal_Replacements(
        [Values(0, 1, 2)] int appliedDeletions)
    {
        return AssertCompensatingDeletionsAsync(appliedDeletions, revisionAdvanced: true);
    }

    private static async Task AssertCompensatingDeletionsAsync(
        int appliedDeletions,
        bool revisionAdvanced)
    {
        using var workspace = new SyncWorkspace();
        var incomplete = new[]
        {
            new DeviceTrack(
                2001,
                "First interrupted insertion",
                null,
                0),
            new DeviceTrack(
                2002,
                "Second interrupted insertion",
                null,
                0)
        };

        var retained = new DeviceTrack(
            2003,
            "Existing track",
            "existing.mp3",
            5);

        var before = CatalogueTestData.Create([.. incomplete, retained]);
        var identity = new LibraryIdentity("id", "binding");
        using var journal = new SyncJournal(workspace.Root, "phone");
        await journal.StartBatchAsync(
            identity,
            before,
            SyncBatch.Create(before, [], incomplete),
            [1, 2],
            CancellationToken.None);

        var actual = before with
        {
            Anchor = revisionAdvanced ? before.Anchor + 1 : before.Anchor,
            Tracks = [.. incomplete.Skip(appliedDeletions), retained]
        };

        var remaining = SyncRecovery.Assess(journal.ReadPending(), actual, []);
        Assert.That(remaining.Select(track => track.Id),
            Is.EqualTo(incomplete.Skip(appliedDeletions).Select(track => track.Id)));

        if (remaining.Count != 0)
        {
            Assert.Throws<IOException>(() => SyncEngine.RequireCompleteAudio(actual));
            await journal.StartBatchAsync(
                identity,
                actual,
                SyncBatch.Create(actual, [], remaining),
                [3, 4],
                CancellationToken.None,
                replacePending: true);

            var retried = journal.ReadPending();
            Assert.That(SyncRecovery.Assess(retried, actual, []).Select(track => track.Id),
                Is.EqualTo(remaining.Select(track => track.Id)));

            actual = actual with { Anchor = actual.Anchor + 1, Tracks = [retained] };
            Assert.That(SyncRecovery.Assess(retried, actual, []), Is.Empty);
        }

        Assert.DoesNotThrow(() => SyncEngine.RequireCompleteAudio(actual));
        journal.Complete();
        Assert.That(journal.HasPending, Is.False);
    }

    [Test]
    public async Task Persists_Metadata_And_Audio_For_Recovery_After_The_Workspace_Is_Gone()
    {
        using var workspace = new SyncWorkspace();
        using var stateRoot = new SyncWorkspace();
        using var fixture = new CatalogueFixture();
        var before = await fixture.ReadAsync();
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
        Assert.That(await File.ReadAllBytesAsync(recovered.LocalPath), Is.EqualTo(await File.ReadAllBytesAsync(SyncTestData.FixturePath)));
        Assert.That(recovered.Metadata.Artwork!.Bytes, Is.EqualTo(track.Metadata.Artwork!.Bytes));
        Assert.DoesNotThrow(() => SyncRecovery.Assess(pending, before, [recovered]));
    }

    [Test]
    public void Distinguishes_Completed_And_Unfilled_Insertions_Without_Replaying_Them([Values] bool incomplete)
    {
        var before = CatalogueTestData.Create([]);
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

    [Test]
    public async Task Cleans_Only_The_Recorded_Staging_Prefix_And_Refuses_Foreign_Data([Values] bool owned)
    {
        var files = new AfcPeer { AllowWrites = true };
        files.Files[SyncPaths.Metadata] = owned ? [1, 2] : [2, 1];
        await using var afc = new AfcClient(files, TimeSpan.FromSeconds(1));
        if (owned)
        {
            await DeviceStaging.RemoveMatchingAsync(
                afc,
                SyncPaths.Metadata,
                [1, 2, 3],
                CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => DeviceStaging.RemoveMatchingAsync(
                afc,
                SyncPaths.Metadata,
                [1, 2, 3],
                CancellationToken.None));
        }

        Assert.That(files.Files.ContainsKey(SyncPaths.Metadata), Is.EqualTo(!owned));
    }

    [Test]
    public void Reconciles_Partially_Applied_Deletions_Without_Replaying_The_Original_Batch()
    {
        var before = CatalogueTestData.Create([
            new DeviceTrack(
                101,
                "First",
                "first.mp3",
                1),
            new DeviceTrack(
                102,
                "Second",
                "second.mp3",
                1)]);

        var pending = new PendingSync(
            2,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [],
            [.. before.Tracks],
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
        var before = CatalogueTestData.Create([]);
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
