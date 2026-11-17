using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncEngineIntegrationTests
{
    private const int RunTimeoutSeconds = 90;
    private const string PendingFilePattern = "pending.json";
    private const string VerifiedFilePattern = "verified-*.json";
    private const string AssetsDirectoryPattern = "assets-*";

    [Test]
    public async Task Dry_Run_Reports_Changes_Without_Starting_Batches_Or_Writing_State()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        CreateSource(source.Root, 1);
        var before = await device.ReadCatalogueAsync();
        var result = await RunAsync(
            device,
            source.Root,
            state.Root,
            dryRun: true);

        Assert.That(result.DryRun, Is.True);
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Delete.Select(track => track.Id),
            Is.EqualTo([SimulatedSyncCatalogue.ExistingTrackId]));
        Assert.That(result.VerifiedBatches, Is.Zero);
        Assert.That(device.Batches, Is.Empty);
        Assert.That(device.FileConnections.SelectMany(connection => connection.WritePackets), Is.Empty);
        Assert.That(device.Files.ContainsKey(SyncPaths.Lock), Is.False);
        Assert.That(Directory.EnumerateFileSystemEntries(state.Root), Is.Empty);
        SyncEngine.RequireUnchanged(before, await device.ReadCatalogueAsync());
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
    }

    [Test]
    public async Task Verifies_All_Addition_Batches_Before_Removing_Obsolete_Music()
    {
        const int additions = SyncProtocol.MaxTrackChanges + 1;
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        var path = CreateSource(source.Root, additions);
        var before = await device.ReadCatalogueAsync();
        var bytes = await File.ReadAllBytesAsync(path);
        var result = await RunAsync(device, source.Root, state.Root);
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

        AssertSettled(device, state.Root, result.VerifiedBatches);
    }

    [Test]
    public async Task Repairs_Only_Artwork_While_Preserving_Existing_Audio_Identity_And_Metadata()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        var path = CreateSource(source.Root, 1, artwork: true);
        var bytes = await File.ReadAllBytesAsync(path);
        device.SetExistingAudio(bytes);
        var before = await device.ReadCatalogueAsync();
        var result = await RunAsync(device, source.Root, state.Root);
        var after = await device.ReadCatalogueAsync();

        Assert.That(result.Plan.Add, Is.Empty);
        Assert.That(result.Plan.Delete, Is.Empty);
        Assert.That(result.Plan.Artwork, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Unchanged, Is.EqualTo(1));
        Assert.That(result.VerifiedBatches, Is.EqualTo(1));
        Assert.That(
            device.Batches.Single().Single().Text(MediaKeys.Operation),
            Is.EqualTo(MediaOperations.UpdateTrack));

        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
        Assert.That(device.Files[SimulatedSyncCatalogue.ExistingTrackPath], Is.EqualTo(bytes));
        var artwork = after.TrackArtwork[SimulatedSyncCatalogue.ExistingTrackId];
        Assert.That(
            device.Files[SyncPaths.ArtworkOriginals + artwork.Path],
            Is.EqualTo(Mp3Metadata.Read(path, Path.GetFileName(path)).Artwork!.Bytes));

        AssertSettled(device, state.Root, 1);
    }

    [Test]
    public async Task Registers_Distinct_Covers_For_Additions_And_Repairs_In_The_Same_Run()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true);
        var path = CreateSource(source.Root, 2, artwork: true);
        var bytes = await File.ReadAllBytesAsync(path);
        device.SetExistingAudio(bytes);
        var before = await device.ReadCatalogueAsync();
        var result = await RunAsync(device, source.Root, state.Root);
        var after = await device.ReadCatalogueAsync();

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
            var cover = after.TrackArtwork[track.Id];
            Assert.That(device.Files[SyncPaths.ArtworkOriginals + cover.Path], Is.EqualTo(Mp3Metadata.Read(path, Path.GetFileName(path)).Artwork!.Bytes));
        }

        AssertSettled(device, state.Root, 2);
    }

    [TestCase(SyncInterruptionPoint.AfterMetadata, false)]
    [TestCase(SyncInterruptionPoint.AfterMetadata, true)]
    [TestCase(SyncInterruptionPoint.AfterAssets, false)]
    [TestCase(SyncInterruptionPoint.AfterAssets, true)]
    public async Task Reconciles_Interrupted_Insertions_From_Their_Durable_Journal(SyncInterruptionPoint interruption, bool artwork)
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice { Interruption = interruption };
        var path = CreateSource(source.Root, 1, artwork);
        var before = await device.ReadCatalogueAsync();
        await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));
        var pending = ReadPending(state.Root);
        var interruptedId = pending.Additions.Single().Id;
        Assert.That(pending.Before.Anchor, Is.EqualTo(before.Anchor));
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);

        // A fresh engine and workspace must reconcile the saved evidence without replaying the signed insertion.
        var result = await RunAsync(device, source.Root, state.Root);
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
            Assert.That(after.Tracks.Single().Id, Is.Not.EqualTo(interruptedId));
            Assert.That(result.VerifiedBatches, Is.EqualTo(1));
            AssertSettled(device, state.Root, 2);
        }
        else
        {
            Assert.That(device.Batches, Has.Count.EqualTo(1));
            Assert.That(after.Tracks.Single().Id, Is.EqualTo(interruptedId));
            Assert.That(result.Plan.Unchanged, Is.EqualTo(1));
            Assert.That(result.VerifiedBatches, Is.Zero);
            AssertSettled(device, state.Root, 1);
        }

        if (artwork)
        {
            var cover = after.TrackArtwork[after.Tracks.Single().Id];
            Assert.That(
                device.Files[SyncPaths.ArtworkOriginals + cover.Path],
                Is.EqualTo(Mp3Metadata.Read(path, Path.GetFileName(path)).Artwork!.Bytes));
        }
    }

    [Test]
    public async Task Resumes_A_Recovery_Interrupted_After_Its_Compensating_Deletion_Commits()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice { Interruption = SyncInterruptionPoint.AfterMetadata };
        CreateSource(source.Root, 1);
        await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));
        var insertedId = ReadPending(state.Root).Additions.Single().Id;
        device.Interruption = SyncInterruptionPoint.AfterMetadata;
        device.InterruptAtBatch = 2;
        await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));
        var cleanup = ReadPending(state.Root);
        Assert.That(cleanup.Additions, Is.Empty);
        Assert.That(cleanup.Deletions.Single().Id, Is.EqualTo(insertedId));

        var result = await RunAsync(device, source.Root, state.Root);
        var after = await device.ReadCatalogueAsync();
        Assert.That(
            device.Batches.Select(batch => batch.Single().Text(MediaKeys.Operation)),
            Is.EqualTo([MediaOperations.InsertTrack, MediaOperations.DeleteTrack, MediaOperations.InsertTrack]));

        Assert.That(after.Tracks, Has.Count.EqualTo(1));
        Assert.That(after.Tracks.Single().Id, Is.Not.EqualTo(insertedId));
        Assert.That(result.VerifiedBatches, Is.EqualTo(1));
        AssertSettled(device, state.Root, 2);
    }

    [Test]
    public async Task Dry_Run_Leaves_An_Interrupted_Batch_And_Its_Journal_Untouched()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice { Interruption = SyncInterruptionPoint.AfterMetadata };
        CreateSource(source.Root, 1);
        await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));
        var record = Directory.GetFiles(state.Root, PendingFilePattern, SearchOption.AllDirectories).Single();
        var saved = await File.ReadAllBytesAsync(record);
        var before = await device.ReadCatalogueAsync();
        var staged = device.Files[SyncPaths.Metadata].ToArray();

        var result = await RunAsync(
            device,
            source.Root,
            state.Root,
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
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true) { Interruption = interruption };
        var path = CreateSource(source.Root, 1, artwork: true);
        device.SetExistingAudio(await File.ReadAllBytesAsync(path));
        var before = await device.ReadCatalogueAsync();
        await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));
        Assert.That(ReadPending(state.Root).Additions.Single().IsArtworkOnly, Is.True);

        var result = await RunAsync(device, source.Root, state.Root);
        var after = await device.ReadCatalogueAsync();
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(device.Batches.SelectMany(batch => batch)
            .All(operation => operation.Text(MediaKeys.Operation) == MediaOperations.UpdateTrack), Is.True);
        Assert.That(device.Batches, Has.Count.EqualTo(interruption == SyncInterruptionPoint.AfterMetadata ? 2 : 1));
        Assert.That(result.Plan.HasChanges, Is.False);
        Assert.That(result.VerifiedBatches, Is.Zero);
        Assert.That(after.TrackArtwork[SimulatedSyncCatalogue.ExistingTrackId].Path, Is.Not.Null);
        AssertSettled(device, state.Root, 1);
    }

    [Test]
    public async Task Reconciles_A_Committed_Deletion_Without_Starting_Another_Sync_Session()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true)
        {
            Interruption = SyncInterruptionPoint.AfterMetadata,
            InterruptAtBatch = 2
        };
        CreateSource(source.Root, 1);
        await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));
        var pending = ReadPending(state.Root);
        Assert.That(pending.Additions, Is.Empty);
        Assert.That(pending.Deletions.Single().Id, Is.EqualTo(SimulatedSyncCatalogue.ExistingTrackId));
        var started = device.StartedSessions;
        var result = await RunAsync(device, source.Root, state.Root);
        Assert.That(result.Plan.HasChanges, Is.False);
        Assert.That(device.StartedSessions, Is.EqualTo(started),
            "Settling an observed deletion must not request a new media import.");
        AssertSettled(device, state.Root, 2);
    }

    [Test]
    public async Task Retains_The_Journal_And_Obsolete_Tracks_When_Permanent_Audio_Verification_Fails()
    {
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true) { CorruptUploadedAudio = true };
        CreateSource(source.Root, 1);
        var exception = await Assert.ThrowsAsync<IOException>(() => RunAsync(device, source.Root, state.Root));

        Assert.That(exception!.Message, Does.Contain("read-back differs"));
        Assert.That(device.Batches, Has.Count.EqualTo(1));
        Assert.That(ReadPending(state.Root).Additions, Has.Length.EqualTo(1));
        Assert.That(device.Files.ContainsKey(SimulatedSyncCatalogue.ExistingTrackPath), Is.True);
        Assert.That(Directory.GetFiles(state.Root, VerifiedFilePattern, SearchOption.AllDirectories), Is.Empty);
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
    }

    private static async Task<SyncResult> RunAsync(
        SimulatedSyncDevice device,
        string source,
        string state,
        bool dryRun = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(RunTimeoutSeconds));
        return await device.RunAsync(
            source,
            state,
            dryRun,
            deadline.Token);
    }

    private static string CreateSource(string root, int count, bool artwork = false)
    {
        var path = Path.Combine(root, "0000.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Album = "Album";
            file.Tag.AlbumArtists = ["Artist"];
            file.Tag.Performers = ["Artist"];
            file.Tag.Composers = ["Composer"];
            file.Tag.Genres = ["Genre"];
            if (artwork)
            {
                file.Tag.Pictures = [new TagLib.Picture
                {
                    Data = [.. FeatureTestData.Png()],
                    MimeType = "image/png",
                    Type = TagLib.PictureType.FrontCover
                }];
            }

            file.Save();
        }

        for (var index = 1; index < count; index++)
        {
            File.Copy(path, Path.Combine(root, $"{index:0000}.mp3"));
        }

        return path;
    }

    private static PendingSync ReadPending(string root)
    {
        using var journal = new SyncJournal(root, SimulatedSyncDevice.Udid, allowPending: true);
        Assert.That(journal.HasPending, Is.True);
        return journal.ReadPending();
    }

    private static void AssertSettled(SimulatedSyncDevice device, string state, int verified)
    {
        Assert.That(Directory.GetFiles(state, PendingFilePattern, SearchOption.AllDirectories), Is.Empty);
        Assert.That(Directory.GetFiles(state, VerifiedFilePattern, SearchOption.AllDirectories), Has.Length.EqualTo(verified));
        Assert.That(Directory.GetDirectories(state, AssetsDirectoryPattern, SearchOption.AllDirectories), Is.Empty);
        Assert.That(device.Connections.All(connection => connection.IsDisposed), Is.True);
        Assert.That(device.FileConnections.All(connection => connection.OpenHandles == 0), Is.True);
    }
}
