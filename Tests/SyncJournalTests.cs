using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncJournalTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Removes_Unjournaled_Copies_After_A_Copy_Failure_And_Preserves_Pending_Assets(bool replacing)
    {
        using var workspace = new SyncWorkspace();
        using var state = new SyncWorkspace();
        var first = FeatureTestData.Covered();
        var second = first with { Id = first.Id - 1, LocalPath = Path.Combine(workspace.Root, "missing.mp3") };
        var before = SyncPlannerTests.Catalogue([]);
        var identity = new LibraryIdentity("id", "binding");
        using var journal = new SyncJournal(state.Root, "phone");
        PendingSync? previous = null;
        byte[]? previousRecord = null;
        if (replacing)
        {
            await journal.StartBatchAsync(
                identity,
                before,
                SyncBatch.Create(before, [first with { Id = first.Id - 2 }], []),
                [1, 2],
                CancellationToken.None);

            previous = journal.ReadPending();
            previousRecord = await File.ReadAllBytesAsync(Path.Combine(journal.DirectoryPath, "pending.json"));
        }

        await Assert.CatchAsync<IOException>(() => journal.StartBatchAsync(
            identity,
            before,
            SyncBatch.Create(before, [first, second], []),
            [3, 4],
            CancellationToken.None,
            replacePending: replacing));

        Assert.That(journal.HasPending, Is.EqualTo(replacing));
        var directories = Directory.GetDirectories(journal.DirectoryPath, "assets-*");
        if (previous is null)
        {
            Assert.That(directories, Is.Empty);
        }
        else
        {
            Assert.That(directories, Is.EqualTo(new[] { journal.Assets(previous) }));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(journal.DirectoryPath, "pending.json")), Is.EqualTo(previousRecord));
            var restored = previous.Additions.Single().Restore(journal.Assets(previous));
            Assert.That(await File.ReadAllBytesAsync(restored.LocalPath), Is.EqualTo(await File.ReadAllBytesAsync(first.LocalPath)));
            Assert.That(restored.Metadata.Artwork!.Bytes, Is.EqualTo(first.Metadata.Artwork!.Bytes));
        }
    }

    [Test]
    public async Task Removes_Unjournaled_Copies_If_Publishing_The_Record_Fails()
    {
        using var workspace = new SyncWorkspace();
        using var journal = new SyncJournal(workspace.Root, "phone");
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "pending.json"));
        var before = SyncPlannerTests.Catalogue([]);
        var failure = await Assert.CatchAsync<Exception>(() => journal.StartBatchAsync(
            new LibraryIdentity("id", "binding"),
            before,
            SyncBatch.Create(before, [FeatureTestData.Covered()], []),
            [1, 2],
            CancellationToken.None));

        Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(journal.HasPending, Is.False);
        Assert.That(Directory.GetDirectories(journal.DirectoryPath, "assets-*"), Is.Empty);
    }

    [Test]
    public async Task Removes_Partial_Copies_When_Journal_Creation_Is_Cancelled()
    {
        using var workspace = new SyncWorkspace();
        using var journal = new SyncJournal(workspace.Root, "phone");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var before = SyncPlannerTests.Catalogue([]);
        await Assert.CatchAsync<OperationCanceledException>(() => journal.StartBatchAsync(
            new LibraryIdentity("id", "binding"),
            before,
            SyncBatch.Create(before, [SyncTestData.Track()], []),
            [1, 2],
            cancellation.Token));

        Assert.That(journal.HasPending, Is.False);
        Assert.That(Directory.GetDirectories(journal.DirectoryPath, "assets-*"), Is.Empty);
    }

    [Test]
    public async Task Refuses_To_Overwrite_Assets_Still_Referenced_By_The_Pending_Operation()
    {
        using var workspace = new SyncWorkspace();
        using var journal = new SyncJournal(workspace.Root, "phone");
        var track = SyncTestData.Track();
        var before = SyncPlannerTests.Catalogue([]);
        var batch = SyncBatch.Create(before, [track], []);
        var identity = new LibraryIdentity("id", "binding");
        await journal.StartBatchAsync(
            identity,
            before,
            batch,
            [1, 2],
            CancellationToken.None);

        var previous = journal.ReadPending();
        var previousRecord = await File.ReadAllBytesAsync(Path.Combine(journal.DirectoryPath, "pending.json"));
        await Assert.ThrowsAsync<IOException>(() => journal.StartBatchAsync(
            identity,
            before,
            batch,
            [3, 4],
            CancellationToken.None,
            replacePending: true));

        Assert.That(await File.ReadAllBytesAsync(Path.Combine(journal.DirectoryPath, "pending.json")), Is.EqualTo(previousRecord));
        var restored = previous.Additions.Single().Restore(journal.Assets(previous));
        Assert.That(await File.ReadAllBytesAsync(restored.LocalPath), Is.EqualTo(await File.ReadAllBytesAsync(track.LocalPath)));
    }

    [Test]
    public void Retains_An_Unverified_Operation_And_Refuses_Blind_Replay()
    {
        using var workspace = new SyncWorkspace();
        var plan = new SyncPlan(
            "phone",
            7,
            [],
            [new(101, "Old", "old.mp3", 5)],
            0);

        using (var journal = new SyncJournal(workspace.Root, "phone"))
            journal.Start(
                new LibraryIdentity("id", "binding"),
                plan,
                [1, 2],
                [3, 4]);

        Assert.Throws<IOException>(() => _ = new SyncJournal(workspace.Root, "phone"));
        var pending = Directory.GetFiles(workspace.Root, "pending.json", SearchOption.AllDirectories).Single();
        Assert.That(File.ReadAllText(pending), Does.Contain("binding").And.Contain("Operation"));
    }

    [Test]
    public void Completes_A_Journal_Only_After_Explicit_Verification()
    {
        using var workspace = new SyncWorkspace();
        using (var journal = new SyncJournal(workspace.Root, "phone"))
        {
            journal.Start(
                new LibraryIdentity("id", "binding"),
                new SyncPlan(
                    "phone",
                    7,
                    [],
                    [],
                    0),
                [1],
                [2]);

            journal.Complete();
        }

        using var next = new SyncJournal(workspace.Root, "phone");
        Assert.That(Directory.GetFiles(workspace.Root, "pending.json", SearchOption.AllDirectories), Is.Empty);
        Assert.That(Directory.GetFiles(workspace.Root, "verified-*.json", SearchOption.AllDirectories), Has.Length.EqualTo(1));
    }

    [Test]
    public void Normalized_Udids_Share_The_Same_Exclusive_Lease()
    {
        using var workspace = new SyncWorkspace();
        using var journal = new SyncJournal(workspace.Root, "00008030-001122334455AABB");
        Assert.Throws<IOException>(() => _ = new SyncJournal(workspace.Root, "00008030001122334455aabb"));
    }
}
