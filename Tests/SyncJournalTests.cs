using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncJournalTests
{
    private const string PendingFileName = "pending.json";
    private const string AssetDirectoryPattern = "assets-*";
    private const string PreparedAudioFileName = "prepared.mp3";
    private const string DeviceId = "phone";
    private static readonly LibraryIdentity Identity = new("id", "binding");
    private SyncWorkspace state = null!;
    private SyncWorkspace workspace = null!;
    private DeviceCatalogue before = null!;

    [SetUp]
    public void SetUp()
    {
        this.state = new SyncWorkspace();
        this.workspace = new SyncWorkspace(this.state.Root);
        this.before = CatalogueTestData.Create([]);
    }

    [TearDown]
    public void TearDown()
    {
        this.workspace.Dispose();
        this.state.Dispose();
    }

    [Test]
    public async Task Adopts_Only_Owned_Workspace_Audio_And_Restores_It_After_Workspace_Disposal([Values] bool owned)
    {
        using var journal = this.CreateJournal();
        var original = FeatureTestData.Covered();
        var sourceBytes = await File.ReadAllBytesAsync(original.LocalPath);
        var coverBytes = original.Metadata.Artwork!.Bytes;
        PreparedTrack restored;
        using (var workspace = new SyncWorkspace(journal.DirectoryPath))
        {
            var track = this.CopyToWorkspace(original, owned, workspace);
            await this.StartBatchAsync(journal, [track]);

            Assert.That(File.Exists(track.LocalPath), Is.EqualTo(!owned));
            restored = RestoreSingleTrack(journal, journal.ReadPending());
            Assert.That(restored.Workspace, Is.Null, "Recovery assets must never be consumed as workspace copies.");
        }

        Assert.That(await File.ReadAllBytesAsync(restored.LocalPath), Is.EqualTo(sourceBytes));
        Assert.That(restored.Metadata.Artwork!.Bytes, Is.EqualTo(coverBytes));
        Assert.That(await File.ReadAllBytesAsync(original.LocalPath), Is.EqualTo(sourceBytes));
        journal.Complete();
        Assert.That(File.Exists(restored.LocalPath), Is.False);
    }

    [Test]
    public async Task Does_Not_Adopt_A_File_Outside_The_Claimed_Workspace()
    {
        using var journal = this.CreateJournal();
        var original = SyncTestData.Track();
        var track = original with { Workspace = this.workspace };
        await this.StartBatchAsync(journal, [track]);

        Assert.That(File.Exists(original.LocalPath), Is.True);
        var restored = RestoreSingleTrack(journal, journal.ReadPending());
        Assert.That(await File.ReadAllBytesAsync(restored.LocalPath),
            Is.EqualTo(await File.ReadAllBytesAsync(original.LocalPath)));
    }

    [Test]
    public async Task Rejects_Corrupt_Owned_Audio_Before_Publishing_The_Journal()
    {
        using var journal = this.CreateJournal();
        var track = this.CopyToWorkspace(SyncTestData.Track());
        var bytes = await File.ReadAllBytesAsync(track.LocalPath);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(track.LocalPath, bytes);
        await Assert.ThatAsync(() => this.StartBatchAsync(journal, [track]), Throws.TypeOf<IOException>()
                .With.Message.EqualTo("Durable recovery audio failed verification."));

        AssertNoPendingAssets(journal);
    }

    [Test]
    public async Task Cancellation_Does_Not_Consume_Source_Or_Prepared_Audio([Values] bool adopt)
    {
        using var journal = this.CreateJournal();
        var original = SyncTestData.Track();
        var track = this.CopyToWorkspace(original, adopt);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.CatchAsync<OperationCanceledException>(() =>
            this.StartBatchAsync(journal, [track], cancellation.Token));

        Assert.That(File.Exists(track.LocalPath), Is.True);
        Assert.That(File.Exists(original.LocalPath), Is.True);
        AssertNoPendingAssets(journal);
    }

    [Test]
    public void Cleans_Abandoned_Workspaces_Only_After_Acquiring_The_Device_Lease()
    {
        string abandoned;
        string retained;
        using (var journal = this.CreateJournal())
        {
            // Simulate a process exit without disposing its workspace.
            abandoned = new SyncWorkspace(journal.DirectoryPath).Root;
            File.WriteAllText(Path.Combine(abandoned, PreparedAudioFileName), "private copy");
            retained = Path.Combine(journal.DirectoryPath, "NoTunes-user-data");
            Directory.CreateDirectory(retained);
            Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "assets-retained"));
            Assert.Throws<IOException>(() => _ = this.CreateJournal());
            Assert.That(Directory.Exists(abandoned), Is.True);
        }

        using var next = this.CreateJournal();
        Assert.That(Directory.Exists(abandoned), Is.False);
        Assert.That(Directory.Exists(retained), Is.True);
        Assert.That(Directory.Exists(Path.Combine(next.DirectoryPath, "assets-retained")), Is.True);
    }

    [Test]
    public async Task Removes_Unjournaled_Copies_After_A_Copy_Failure_And_Preserves_Pending_Assets(
        [Values] bool replacing,
        [Values] bool adopt)
    {
        var first = FeatureTestData.Covered();
        var second = first with { Id = first.Id - 1, LocalPath = Path.Combine(this.workspace.Root, "missing.mp3") };
        using var journal = this.CreateJournal();
        PendingSync? previous = null;
        byte[]? previousRecord = null;
        if (replacing)
        {
            await this.StartBatchAsync(journal, [first with { Id = first.Id - 2 }]);

            previous = journal.ReadPending();
            previousRecord = await ReadPendingRecordAsync(journal);
        }

        var prepared = this.CopyToWorkspace(first, adopt);
        await Assert.CatchAsync<IOException>(() =>
            this.StartBatchAsync(journal, [prepared, second], replacePending: replacing));

        Assert.That(journal.HasPending, Is.EqualTo(replacing));
        Assert.That(File.Exists(first.LocalPath), Is.True);
        Assert.That(File.Exists(prepared.LocalPath), Is.EqualTo(!adopt));

        var directories = Directory.GetDirectories(journal.DirectoryPath, AssetDirectoryPattern);
        if (previous is null)
        {
            Assert.That(directories, Is.Empty);
        }
        else
        {
            Assert.That(directories, Is.EqualTo([journal.Assets(previous)]));
            await AssertPendingAssetsAsync(
                journal,
                previous,
                previousRecord,
                first);
        }
    }

    [Test]
    public async Task Removes_Unjournaled_Assets_If_Publishing_The_Record_Fails([Values] bool adopt)
    {
        using var journal = this.CreateJournal();
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, PendingFileName));
        var original = FeatureTestData.Covered();
        var track = this.CopyToWorkspace(original, adopt);
        var failure = await Assert.CatchAsync<Exception>(() => this.StartBatchAsync(journal, [track]));

        Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(File.Exists(track.LocalPath), Is.EqualTo(!adopt));
        Assert.That(File.Exists(original.LocalPath), Is.True);
        AssertNoPendingAssets(journal);
    }

    [Test]
    public async Task Refuses_To_Overwrite_Assets_Still_Referenced_By_The_Pending_Operation()
    {
        using var journal = this.CreateJournal();
        var track = SyncTestData.Track();
        var batch = SyncBatch.Create(this.before, [track], []);
        await this.StartBatchAsync(journal, batch);

        var previous = journal.ReadPending();
        var previousRecord = await ReadPendingRecordAsync(journal);
        await Assert.ThrowsAsync<IOException>(() => journal.StartBatchAsync(
            Identity,
            this.before,
            batch,
            [3, 4],
            CancellationToken.None,
            replacePending: true));

        await AssertPendingAssetsAsync(
            journal,
            previous,
            previousRecord,
            track);
    }

    [Test]
    public void Retains_An_Unverified_Operation_And_Refuses_Blind_Replay()
    {
        using (var journal = this.CreateJournal())
        {
            this.StartLegacyJournal(journal);
        }

        Assert.Throws<IOException>(() => _ = this.CreateJournal());
        var pending = Directory.GetFiles(this.state.Root, PendingFileName, SearchOption.AllDirectories).Single();
        Assert.That(File.ReadAllText(pending), Does.Contain("binding").And.Contain("Operation"));
    }

    [Test]
    public void Completes_A_Journal_Only_After_Explicit_Verification()
    {
        using (var journal = this.CreateJournal())
        {
            this.StartLegacyJournal(journal);
            journal.Complete();
        }

        using var next = this.CreateJournal();
        Assert.That(Directory.GetFiles(this.state.Root, PendingFileName, SearchOption.AllDirectories), Is.Empty);
        Assert.That(Directory.GetFiles(this.state.Root, "verified-*.json", SearchOption.AllDirectories),
            Has.Length.EqualTo(1));
    }

    [Test]
    public void Normalized_Udids_Share_The_Same_Exclusive_Lease()
    {
        using var journal = this.CreateJournal("00008030-001122334455AABB");
        Assert.Throws<IOException>(() => _ = this.CreateJournal("00008030001122334455aabb"));
    }

    private SyncJournal CreateJournal(string device = DeviceId)
    {
        return new SyncJournal(this.state.Root, device);
    }

    private Task StartBatchAsync(
        SyncJournal journal,
        PreparedTrack[] additions,
        CancellationToken cancellationToken = default,
        bool replacePending = false)
    {
        return this.StartBatchAsync(
            journal,
            SyncBatch.Create(this.before, additions, []),
            cancellationToken,
            replacePending);
    }

    private Task StartBatchAsync(
        SyncJournal journal,
        SyncBatch batch,
        CancellationToken cancellationToken = default,
        bool replacePending = false)
    {
        return journal.StartBatchAsync(
            Identity,
            this.before,
            batch,
            [1, 2],
            cancellationToken,
            replacePending);
    }

    private void StartLegacyJournal(SyncJournal journal)
    {
        var plan = new SyncPlan(
            DeviceId,
            this.before.Anchor,
            [],
            [],
            0);

        journal.Start(
            Identity,
            plan,
            [1, 2],
            [3, 4]);
    }

    private static async Task AssertPendingAssetsAsync(
        SyncJournal journal,
        PendingSync previous,
        byte[]? previousRecord,
        PreparedTrack original)
    {
        var record = await ReadPendingRecordAsync(journal);
        Assert.That(record, Is.EqualTo(previousRecord));
        var restored = RestoreSingleTrack(journal, previous);
        Assert.That(await File.ReadAllBytesAsync(restored.LocalPath),
            Is.EqualTo(await File.ReadAllBytesAsync(original.LocalPath)));

        if (original.Metadata.Artwork is { } artwork)
        {
            Assert.That(restored.Metadata.Artwork!.Bytes, Is.EqualTo(artwork.Bytes));
        }
    }

    private PreparedTrack CopyToWorkspace(PreparedTrack original, bool owned = true, SyncWorkspace? workspace = null)
    {
        workspace ??= this.workspace;
        var path = Path.Combine(workspace.Root, PreparedAudioFileName);
        File.Copy(original.LocalPath, path);
        return original with { LocalPath = path, Workspace = owned ? workspace : null };
    }

    private static PreparedTrack RestoreSingleTrack(SyncJournal journal, PendingSync pending)
    {
        return pending.Additions.Single().Restore(journal.Assets(pending));
    }

    private static Task<byte[]> ReadPendingRecordAsync(SyncJournal journal)
    {
        return File.ReadAllBytesAsync(Path.Combine(journal.DirectoryPath, PendingFileName));
    }

    private static void AssertNoPendingAssets(SyncJournal journal)
    {
        Assert.That(journal.HasPending, Is.False);
        Assert.That(Directory.GetDirectories(journal.DirectoryPath, AssetDirectoryPattern), Is.Empty);
    }
}
