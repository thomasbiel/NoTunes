using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncEngineIntegrationTests
{
    private SyncWorkspace source = null!;
    private SyncWorkspace state = null!;

    [SetUp]
    public void SetUp()
    {
        this.source = new SyncWorkspace();
        this.state = new SyncWorkspace();
    }

    [TearDown]
    public void TearDown()
    {
        this.state.Dispose();
        this.source.Dispose();
    }

    [Test]
    public async Task Skips_Invalid_Source_Files_And_Defers_Deletions([Values] bool dryRun, [Values] bool invalidCover)
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        var validPath = this.CreateSource(1);
        const string invalidName = "!invalid.mp3";
        if (invalidCover)
        {
            SourceTestData.WriteTrack(
                this.source.Root,
                invalidName,
                "Album",
                "Artist",
                file => file.Tag.Pictures = [FeatureTestData.FrontCover([1, 2, 3, 4])]);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(this.source.Root, invalidName), "Not an MP3");
        }

        var (before, result, after) = await this.RunAndReadAsync(device, dryRun);

        Assert.That(result.Plan.Add.Select(track => track.RelativePath), Is.EqualTo([Path.GetFileName(validPath)]));
        Assert.That(result.Plan.Delete, Is.Empty);
        Assert.That(result.Plan.Skipped.Single().RelativePath, Is.EqualTo(invalidName));
        Assert.That(result.Plan.Skipped.Single().ArtworkOnly, Is.False);
        Assert.That(result.Plan.Skipped.Single().Reason, Is.Not.Empty);
        Assert.That(result.Plan.DeferredDeletions, Is.EqualTo(before.Tracks));
        Assert.That(result.VerifiedBatches, Is.EqualTo(dryRun ? 0 : 1));
        Assert.That(after.Tracks, Has.Count.EqualTo(dryRun ? 1 : 2));
        Assert.That(after.Tracks.Single(track => track.Id == SimulatedSyncCatalogue.ExistingTrackId),
            Is.EqualTo(before.Tracks.Single()));

        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
        Assert.That(device.Files.ContainsKey(SimulatedSyncCatalogue.ExistingTrackPath), Is.True);
        if (!dryRun)
        {
            var added = after.Tracks.Single(track => track.Id != SimulatedSyncCatalogue.ExistingTrackId);
            Assert.That(device.Files[added.Path!], Is.EqualTo(await File.ReadAllBytesAsync(validPath)));
        }

        this.AssertSettled(device, dryRun ? 0 : 1);
    }

    [Test]
    public async Task Preserves_All_Existing_Tracks_When_Every_Addition_Is_Invalid([Values] bool dryRun)
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        await File.WriteAllTextAsync(Path.Combine(this.source.Root, "invalid.mp3"), "Not an MP3");

        var (before, result, after) = await this.RunAndReadAsync(device, dryRun);

        Assert.That(result.Plan.HasChanges, Is.False);
        Assert.That(result.Plan.Add, Is.Empty);
        Assert.That(result.Plan.Delete, Is.Empty);
        Assert.That(result.Plan.Skipped, Has.Count.EqualTo(1));
        Assert.That(result.Plan.DeferredDeletions, Is.EqualTo(before.Tracks));
        Assert.That(result.VerifiedBatches, Is.Zero);
        Assert.That(device.StartedSessions, Is.Zero);
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(after.Anchor, Is.EqualTo(before.Anchor));
        this.AssertSettled(device, 0);
    }

    [Test]
    public async Task Skips_A_Broken_Cover_Repair_Only_When_Requested_And_Continues_Audio_Additions(
        [Values] bool dryRun,
        [Values] bool repairCovers)
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        this.CreateSource(1);
        var path = SourceTestData.WriteTrack(
            this.source.Root,
            "covered.mp3",
            "Album",
            "Artist",
            file => file.Tag.Pictures = [FeatureTestData.FrontCover([1, 2, 3, 4])]);

        var bytes = await File.ReadAllBytesAsync(path);
        device.SetExistingAudio(bytes);

        var (before, result, after) = await this.RunAndReadAsync(device, dryRun, repairCovers);

        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Artwork, Is.Empty);
        Assert.That(result.Plan.Skipped, Has.Count.EqualTo(repairCovers ? 1 : 0));
        if (repairCovers)
        {
            Assert.That(result.Plan.Skipped.Single().RelativePath, Is.EqualTo("covered.mp3"));
            Assert.That(result.Plan.Skipped.Single().ArtworkOnly, Is.True);
        }

        Assert.That(result.Plan.Unchanged, Is.EqualTo(1));
        Assert.That(result.Plan.DeferredDeletions, Is.Empty);
        Assert.That(result.VerifiedBatches, Is.EqualTo(dryRun ? 0 : 1));
        Assert.That(after.Tracks.Single(track => track.Id == SimulatedSyncCatalogue.ExistingTrackId),
            Is.EqualTo(before.Tracks.Single()));

        Assert.That(device.Files[SimulatedSyncCatalogue.ExistingTrackPath], Is.EqualTo(bytes));
        Assert.That(device.Batches.SelectMany(batch => batch)
            .All(operation => operation.Text(MediaKeys.Operation) == MediaOperations.InsertTrack), Is.True);

        this.AssertSettled(device, dryRun ? 0 : 1);
    }

    [Test]
    public async Task Still_Stops_For_An_Unresolved_Group_Before_Any_Device_Write()
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        device.AddAlbumArtist(
            22,
            1022,
            "Artist",
            "Other Sort");

        this.CreateSource(1);

        var before = await device.ReadCatalogueAsync();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => this.RunAsync(device));
        var after = await device.ReadCatalogueAsync();

        Assert.That(exception!.Message, Does.Contain("Cannot uniquely assign"));
        Assert.That(device.StartedSessions, Is.Zero);
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(device.FileConnections.SelectMany(connection => connection.WritePackets), Is.Empty);
        this.AssertSettled(device, 0);
    }

    [Test]
    public async Task Synchronizes_After_Automatic_Or_Interactive_Group_Resolution([Values] bool ask)
    {
        var prompts = 0;

        using var device = new SimulatedSyncDevice
        {
            PromptForGroupName = (_, _) =>
            {
                prompts++;
                return ValueTask.FromResult<string?>("Artist");
            }
        };

        device.AddAlbumArtist(
            21,
            ask ? 1021 : 1011,
            "Ambiguous Artist",
            "First Sort");

        if (ask)
        {
            device.AddAlbumArtist(
                22,
                1022,
                "Ambiguous Artist",
                "Second Sort");
        }

        var path = this.CreateSource(1);
        if (ask)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.AlbumArtists = ["Ambiguous Artist"];
            file.Save();
        }

        var bytes = await File.ReadAllBytesAsync(path);
        var (before, result, after) = await this.RunAndReadAsync(device);
        Assert.That(prompts, Is.EqualTo(ask ? 1 : 0));
        Assert.That(result.VerifiedBatches, Is.EqualTo(1));
        Assert.That(after.Tracks.Single().Metadata!.AlbumArtistRowId, Is.EqualTo(11));
        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
        Assert.That(after.ArtistGroups, Is.EqualTo(before.ArtistGroups));
        Assert.That(device.Files[after.Tracks.Single().Path!], Is.EqualTo(bytes));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
        this.AssertSettled(device, 1);
    }

    private const int RunTimeoutSeconds = 90;
    private const string PendingFilePattern = "pending.json";
    private const string VerifiedFilePattern = "verified-*.json";
    private const string AssetsDirectoryPattern = "assets-*";

    [Test]
    public async Task Dry_Run_Reports_Changes_Without_Starting_Batches_Or_Writing_State()
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        this.CreateSource(1);

        var before = await device.ReadCatalogueAsync();
        var result = await this.RunAsync(device, dryRun: true);

        Assert.That(result.DryRun, Is.True);
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(
            result.Plan.Delete.Select(track => track.Id),
            Is.EqualTo([SimulatedSyncCatalogue.ExistingTrackId]));

        Assert.That(result.VerifiedBatches, Is.Zero);
        SimulatedSyncDeviceAssertions.HasNoWrites(device);
        Assert.That(Directory.EnumerateFileSystemEntries(this.state.Root), Is.Empty);
        SyncEngine.RequireUnchanged(before, await device.ReadCatalogueAsync());
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
    }

    [Test]
    public async Task Verifies_All_Addition_Batches_Before_Removing_Obsolete_Music()
    {
        const int additions = SyncProtocol.MaxTrackChanges + 1;
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);

        var path = this.CreateSource(additions);
        var before = await device.ReadCatalogueAsync();
        var bytes = await File.ReadAllBytesAsync(path);
        var result = await this.RunAsync(device);
        var after = await device.ReadCatalogueAsync();

        Assert.That(result.Plan.Add, Has.Count.EqualTo(additions));
        Assert.That(
            result.Plan.Delete.Select(track => track.Id),
            Is.EqualTo([SimulatedSyncCatalogue.ExistingTrackId]));

        Assert.That(result.VerifiedBatches, Is.EqualTo(3));
        Assert.That(
            device.Batches.Select(batch => batch.Length),
            Is.EqualTo([SyncProtocol.MaxTrackChanges, 1, 1]));

        Assert.That(device.Batches.Take(2).SelectMany(batch => batch)
            .All(operation => operation.Text(MediaKeys.Operation) == MediaOperations.InsertTrack), Is.True);

        Assert.That(
            device.Batches.Last().Single().Text(MediaKeys.Operation),
            Is.EqualTo(MediaOperations.DeleteTrack));

        Assert.That(after.Anchor, Is.EqualTo(before.Anchor + result.VerifiedBatches));
        Assert.That(after.Tracks, Has.Count.EqualTo(additions));
        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
        Assert.That(device.Files.ContainsKey(SimulatedSyncCatalogue.ExistingTrackPath), Is.False);
        foreach (var track in after.Tracks)
        {
            Assert.That(device.Files[track.Path!], Is.EqualTo(bytes));
        }

        this.AssertSettled(device, result.VerifiedBatches);
    }

    [Test]
    public async Task Only_Transfers_Covers_For_New_Tracks_By_Default([Values] bool dryRun, [Values] bool addTrack)
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        var (path, bytes) = await this.PrepareCoverRepairAsync(device, addTrack ? 2 : 1);

        var (before, result, after) = await this.RunAndReadAsync(device, dryRun);

        Assert.That(result.Plan.Artwork, Is.Empty);
        Assert.That(result.Plan.Skipped, Is.Empty);
        Assert.That(result.Plan.Delete, Is.Empty);
        Assert.That(result.Plan.Unchanged, Is.EqualTo(1));
        Assert.That(result.Plan.Add, Has.Count.EqualTo(addTrack ? 1 : 0));
        Assert.That(result.VerifiedBatches, Is.EqualTo(addTrack && !dryRun ? 1 : 0));
        Assert.That(after.Tracks.Single(track => track.Id == SimulatedSyncCatalogue.ExistingTrackId),
            Is.EqualTo(before.Tracks.Single()));

        Assert.That(after.TrackArtwork.ContainsKey(SimulatedSyncCatalogue.ExistingTrackId), Is.False);
        Assert.That(device.Files[SimulatedSyncCatalogue.ExistingTrackPath], Is.EqualTo(bytes));
        if (addTrack && !dryRun)
        {
            var added = after.Tracks.Single(track => track.Id != SimulatedSyncCatalogue.ExistingTrackId);
            SimulatedSyncDeviceAssertions.HasArtworkFrom(
                device,
                after,
                added.Id,
                path);
        }
        else
        {
            SimulatedSyncDeviceAssertions.HasNoWrites(device);
        }

        this.AssertSettled(device, addTrack && !dryRun ? 1 : 0);
    }

    [Test]
    public async Task Repairs_Only_Artwork_While_Preserving_Existing_Audio_Identity_And_Metadata([Values] bool dryRun)
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);

        var (path, bytes) = await this.PrepareCoverRepairAsync(device);
        var (before, result, after) = await this.RunAndReadAsync(device, dryRun, repairCovers: true);

        Assert.That(result.Plan.Add, Is.Empty);
        Assert.That(result.Plan.Delete, Is.Empty);
        Assert.That(result.Plan.Artwork, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Unchanged, Is.EqualTo(1));
        if (dryRun)
        {
            Assert.That(result.VerifiedBatches, Is.Zero);
            SimulatedSyncDeviceAssertions.HasNoWrites(device);
            SyncEngine.RequireUnchanged(before, after);
            this.AssertSettled(device, 0);
            return;
        }

        Assert.That(result.VerifiedBatches, Is.EqualTo(1));
        Assert.That(
            device.Batches.Single().Single().Text(MediaKeys.Operation),
            Is.EqualTo(MediaOperations.UpdateTrack));

        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
        Assert.That(device.Files[SimulatedSyncCatalogue.ExistingTrackPath], Is.EqualTo(bytes));
        SimulatedSyncDeviceAssertions.HasArtworkFrom(
            device,
            after,
            SimulatedSyncCatalogue.ExistingTrackId,
            path);

        this.AssertSettled(device, 1);
    }

    [Test]
    public async Task Registers_Distinct_Covers_For_Additions_And_Repairs_In_The_Same_Run()
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);

        var (path, bytes) = await this.PrepareCoverRepairAsync(device, 2);
        var (before, result, after) = await this.RunAndReadAsync(device, repairCovers: true);

        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Artwork, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Delete, Is.Empty);
        Assert.That(result.VerifiedBatches, Is.EqualTo(2));
        Assert.That(
            device.Batches.Select(batch => batch.Single().Text(MediaKeys.Operation)),
            Is.EqualTo([MediaOperations.InsertTrack, MediaOperations.UpdateTrack]));

        Assert.That(after.Tracks.Single(track => track.Id == SimulatedSyncCatalogue.ExistingTrackId), Is.EqualTo(before.Tracks.Single()));
        Assert.That(after.TrackArtwork.Values.Select(cover => cover.CacheId).Distinct().Count(), Is.EqualTo(2));
        foreach (var track in after.Tracks)
        {
            Assert.That(device.Files[track.Path!], Is.EqualTo(bytes));
            SimulatedSyncDeviceAssertions.HasArtworkFrom(
                device,
                after,
                track.Id,
                path);
        }

        this.AssertSettled(device, 2);
    }

    [Test]
    public async Task Reconciles_Interrupted_Insertions_From_Their_Durable_Journal(
        [Values(SyncInterruptionPoint.AfterMetadata, SyncInterruptionPoint.AfterAssets)] SyncInterruptionPoint interruption,
        [Values] bool artwork)
    {
        using var device = new SimulatedSyncDevice { Interruption = interruption };

        var path = this.CreateSource(1, artwork);
        var before = await device.ReadCatalogueAsync();
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));
        var pending = this.ReadPending();
        var interruptedId = pending.Additions.Single().Id;
        Assert.That(pending.Before.Anchor, Is.EqualTo(before.Anchor));
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);

        // A fresh engine and workspace must reconcile the saved evidence without replaying the signed insertion.
        var result = await this.RunAsync(device);
        var after = await device.ReadCatalogueAsync();
        Assert.That(after.Tracks, Has.Count.EqualTo(1));
        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
        Assert.That(device.Files[after.Tracks.Single().Path!], Is.EqualTo(await File.ReadAllBytesAsync(path)));
        if (interruption == SyncInterruptionPoint.AfterMetadata)
        {
            Assert.That(
                device.Batches.Select(batch => batch.Single().Text(MediaKeys.Operation)),
                Is.EqualTo([MediaOperations.InsertTrack, MediaOperations.DeleteTrack, MediaOperations.InsertTrack]));

            Assert.That(device.Batches[1].Single().Number(MediaKeys.PersistentId), Is.EqualTo(interruptedId));
            this.AssertRecoveredReplacement(
                device,
                result,
                after,
                interruptedId);
        }
        else
        {
            Assert.That(device.Batches, Has.Count.EqualTo(1));
            Assert.That(after.Tracks.Single().Id, Is.EqualTo(interruptedId));
            Assert.That(result.Plan.Unchanged, Is.EqualTo(1));
            Assert.That(result.VerifiedBatches, Is.Zero);
            this.AssertSettled(device, 1);
        }

        if (artwork)
        {
            SimulatedSyncDeviceAssertions.HasArtworkFrom(
                device,
                after,
                after.Tracks.Single().Id,
                path);
        }
    }

    [Test]
    public async Task Resumes_A_Recovery_Interrupted_After_Its_Compensating_Deletion_Commits()
    {
        using var device = new SimulatedSyncDevice { Interruption = SyncInterruptionPoint.AfterMetadata };

        this.CreateSource(1);
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));
        var insertedId = this.ReadPending().Additions.Single().Id;
        device.Interruption = SyncInterruptionPoint.AfterMetadata;
        device.InterruptAtBatch = 2;
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));
        var cleanup = this.ReadPending();
        Assert.That(cleanup.Additions, Is.Empty);
        Assert.That(cleanup.Deletions.Single().Id, Is.EqualTo(insertedId));

        var result = await this.RunAsync(device);
        var after = await device.ReadCatalogueAsync();
        Assert.That(
            device.Batches.Select(batch => batch.Single().Text(MediaKeys.Operation)),
            Is.EqualTo([MediaOperations.InsertTrack, MediaOperations.DeleteTrack, MediaOperations.InsertTrack]));

        Assert.That(after.Tracks, Has.Count.EqualTo(1));
        this.AssertRecoveredReplacement(
            device,
            result,
            after,
            insertedId);
    }

    [Test]
    public async Task Dry_Run_Leaves_An_Interrupted_Batch_And_Its_Journal_Untouched()
    {
        using var device = new SimulatedSyncDevice { Interruption = SyncInterruptionPoint.AfterMetadata };

        this.CreateSource(1);
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));
        var record = Directory.GetFiles(this.state.Root, PendingFilePattern, SearchOption.AllDirectories).Single();
        var saved = await File.ReadAllBytesAsync(record);
        var before = await device.ReadCatalogueAsync();
        var staged = device.Files[SyncPaths.Metadata].ToArray();

        var result = await this.RunAsync(device,
            dryRun: true);

        Assert.That(result.DryRun, Is.True);
        Assert.That(result.VerifiedBatches, Is.Zero);
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Delete.Single().IsIncomplete, Is.True);
        Assert.That(device.Batches, Has.Count.EqualTo(1));
        Assert.That(await File.ReadAllBytesAsync(record), Is.EqualTo(saved));
        Assert.That(device.Files[SyncPaths.Metadata], Is.EqualTo(staged));
        SyncEngine.RequireUnchanged(before, await device.ReadCatalogueAsync());
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
    }

    [TestCase(SyncInterruptionPoint.AfterMetadata)]
    [TestCase(SyncInterruptionPoint.AfterAssets)]
    public async Task Recovers_An_Interrupted_Cover_Repair_Without_Removing_The_Existing_Track(SyncInterruptionPoint interruption)
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true) { Interruption = interruption };

        var (path, before) = await this.InterruptCoverRepairAsync(device);
        Assert.That(this.ReadPending().Additions.Single().IsArtworkOnly, Is.True);

        var result = await this.RunAsync(device);
        var after = await device.ReadCatalogueAsync();
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(device.Batches.SelectMany(batch => batch)
            .All(operation => operation.Text(MediaKeys.Operation) == MediaOperations.UpdateTrack), Is.True);

        Assert.That(device.Batches, Has.Count.EqualTo(interruption == SyncInterruptionPoint.AfterMetadata ? 2 : 1));
        Assert.That(result.Plan.HasChanges, Is.False);
        Assert.That(result.VerifiedBatches, Is.Zero);
        SimulatedSyncDeviceAssertions.HasArtworkFrom(
            device,
            after,
            SimulatedSyncCatalogue.ExistingTrackId,
            path);

        this.AssertSettled(device, 1);
    }

    [Test]
    public async Task Resumes_A_Cover_Recovery_After_Replacing_Its_Durable_Assets()
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true) { Interruption = SyncInterruptionPoint.AfterMetadata };

        var (path, before) = await this.InterruptCoverRepairAsync(device);
        var previousCover = Directory.GetFiles(this.state.Root, "*.jpg", SearchOption.AllDirectories).Single();

        device.Interruption = SyncInterruptionPoint.AfterMetadata;
        device.InterruptAtBatch = 2;
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));
        Assert.That(File.Exists(previousCover), Is.False);
        var replacement = Directory.GetFiles(this.state.Root, "*.jpg", SearchOption.AllDirectories).Single();
        Assert.That(replacement, Is.Not.EqualTo(previousCover));
        Assert.That(this.ReadPending().Additions.Single().IsArtworkOnly, Is.True);

        await this.RunAsync(device);
        var after = await device.ReadCatalogueAsync();
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(device.Batches, Has.Count.EqualTo(3));
        Assert.That(device.Batches.SelectMany(batch => batch)
            .All(operation => operation.Text(MediaKeys.Operation) == MediaOperations.UpdateTrack), Is.True);

        SimulatedSyncDeviceAssertions.HasArtworkFrom(
            device,
            after,
            SimulatedSyncCatalogue.ExistingTrackId,
            path);

        this.AssertSettled(device, 1);
    }

    [Test]
    public async Task Reconciles_A_Committed_Deletion_Without_Starting_Another_Sync_Session()
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true)
        {
            Interruption = SyncInterruptionPoint.AfterMetadata,
            InterruptAtBatch = 2
        };

        this.CreateSource(1);
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));
        var pending = this.ReadPending();
        Assert.That(pending.Additions, Is.Empty);
        Assert.That(pending.Deletions.Single().Id, Is.EqualTo(SimulatedSyncCatalogue.ExistingTrackId));
        var started = device.StartedSessions;
        var result = await this.RunAsync(device);
        Assert.That(result.Plan.HasChanges, Is.False);
        Assert.That(
            device.StartedSessions,
            Is.EqualTo(started),
            "Settling an observed deletion must not request a new media import.");

        this.AssertSettled(device, 2);
    }

    [Test]
    public async Task Retains_The_Journal_And_Obsolete_Tracks_When_Permanent_Audio_Verification_Fails()
    {
        using var device = new SimulatedSyncDevice(keepExistingTrack: true) { CorruptUploadedAudio = true };

        this.CreateSource(1);
        var exception = await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device));

        Assert.That(exception!.Message, Does.Contain("audio hash differs"));
        Assert.That(device.Batches, Has.Count.EqualTo(1));
        Assert.That(this.ReadPending().Additions, Has.Length.EqualTo(1));
        Assert.That(device.Files.ContainsKey(SimulatedSyncCatalogue.ExistingTrackPath), Is.True);
        Assert.That(Directory.GetFiles(this.state.Root, VerifiedFilePattern, SearchOption.AllDirectories), Is.Empty);
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
    }

    private async Task<SyncResult> RunAsync(
        SimulatedSyncDevice device,
        bool dryRun = false,
        bool repairCovers = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(RunTimeoutSeconds));
        return await device.RunAsync(
            this.source.Root,
            this.state.Root,
            dryRun,
            deadline.Token,
            repairCovers);
    }

    [Test]
    public async Task Transfers_All_Tracks_When_Metadata_Limits_Shorten_A_Batch()
    {
        using var device = new SimulatedSyncDevice();
        const int count = 180;
        this.CreateSource(count,
            configure: file =>
            {
                file.Tag.Title = new string('界', Mp3Metadata.MaximumTextLength);
                file.Tag.TitleSort = file.Tag.Title;
                file.Tag.Comment = new string('言', Mp3Metadata.MaximumTextLength);
            });

        var result = await this.RunAsync(device);
        var after = await device.ReadCatalogueAsync();
        Assert.That(device.Batches.First(), Has.Length.LessThan(count), "The byte limit must shorten the first batch.");
        Assert.That(device.Batches.Sum(batch => batch.Length), Is.EqualTo(count));
        Assert.That(after.Tracks, Has.Count.EqualTo(count));
        Assert.That(after.Tracks.All(track => !track.IsIncomplete), Is.True);
        Assert.That(result.VerifiedBatches, Is.GreaterThan(1));
        this.AssertSettled(device, result.VerifiedBatches);
    }

    private async Task<(DeviceCatalogue Before, SyncResult Result, DeviceCatalogue After)> RunAndReadAsync(
        SimulatedSyncDevice device,
        bool dryRun = false,
        bool repairCovers = false)
    {
        var before = await device.ReadCatalogueAsync();
        var result = await this.RunAsync(device, dryRun, repairCovers);
        var after = await device.ReadCatalogueAsync();
        return (before, result, after);
    }

    private async Task<(string Path, byte[] Bytes)> PrepareCoverRepairAsync(
        SimulatedSyncDevice device,
        int sourceCount = 1)
    {
        var path = this.CreateSource(sourceCount, artwork: true);
        var bytes = await File.ReadAllBytesAsync(path);
        device.SetExistingAudio(bytes);
        return (path, bytes);
    }

    private async Task<(string Path, DeviceCatalogue Before)> InterruptCoverRepairAsync(SimulatedSyncDevice device)
    {
        var (path, _) = await this.PrepareCoverRepairAsync(device);
        var before = await device.ReadCatalogueAsync();
        await Assert.ThrowsAsync<IOException>(() => this.RunAsync(device, repairCovers: true));
        return (path, before);
    }

    private void AssertRecoveredReplacement(
        SimulatedSyncDevice device,
        SyncResult result,
        DeviceCatalogue after,
        long interruptedId)
    {
        Assert.That(after.Tracks.Single().Id, Is.Not.EqualTo(interruptedId));
        Assert.That(result.VerifiedBatches, Is.EqualTo(1));
        this.AssertSettled(device, 2);
    }

    private string CreateSource(int count, bool artwork = false, Action<TagLib.File>? configure = null)
    {
        return SourceTestData.WriteSyncTracks(
            this.source.Root,
            count,
            artwork,
            configure);
    }

    private PendingSync ReadPending()
    {
        using var journal = new SyncJournal(this.state.Root, SimulatedSyncDevice.Udid, allowPending: true);
        Assert.That(journal.HasPending, Is.True);
        return journal.ReadPending();
    }

    private void AssertSettled(SimulatedSyncDevice device, int verified)
    {
        Assert.That(Directory.GetFiles(this.state.Root, PendingFilePattern, SearchOption.AllDirectories), Is.Empty);
        Assert.That(Directory.GetFiles(this.state.Root, VerifiedFilePattern, SearchOption.AllDirectories), Has.Length.EqualTo(verified));
        Assert.That(Directory.GetDirectories(this.state.Root, AssetsDirectoryPattern, SearchOption.AllDirectories), Is.Empty);
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
        Assert.That(device.FileConnections.All(connection => connection.OpenHandles == 0), Is.True);
    }
}
