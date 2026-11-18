using System.Text.Json;
using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CatalogueGroupCompatibilityTests
{
    private const long SharedArtistId = 1011;
    private const long SharedAlbumId = 1012;
    private const string ArtistName = "Artist";
    private const string ArtistAlias = "Artist.";
    private const string AlbumName = "Album";

    [Test]
    public async Task Reads_Shared_Group_Sync_Ids_Without_Losing_Track_Associations()
    {
        using var fixture = CreateSharedGroups();
        var catalogue = await fixture.ReadAsync();
        Assert.That(catalogue.Artists[ArtistName], Is.Zero);
        Assert.That(catalogue.Artists[ArtistAlias], Is.Zero);
        Assert.That(catalogue.Albums[CatalogueReader.GroupKey(AlbumName, ArtistName)], Is.Zero);
        Assert.That(catalogue.Albums[CatalogueReader.GroupKey(AlbumName, ArtistAlias)], Is.Zero);
        Assert.That(catalogue.OccupiedIds, Does.Contain(SharedArtistId).And.Contain(SharedAlbumId));
        Assert.That(catalogue.Tracks.Single().Metadata!.AlbumArtistRowId, Is.EqualTo(11));
        Assert.That(catalogue.Tracks.Single().Metadata!.AlbumRowId, Is.EqualTo(12));

        var protectedTrack = catalogue.ProtectedItems.Single(item => item.ItemId == 5);
        Assert.That(protectedTrack.Metadata!.AlbumArtistRowId, Is.EqualTo(22));
        Assert.That(protectedTrack.Metadata!.AlbumRowId, Is.EqualTo(13));
    }

    [Test]
    public async Task Prepares_Unrelated_Additions_Alongside_Shared_Group_Sync_Ids()
    {
        using var fixture = CreateSharedGroups();
        var catalogue = await fixture.ReadAsync();
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            "Independent Album",
            "Independent Artist");

        var prepared = await SourceTestData.PrepareAsync(source.Root, catalogue, output);
        Assert.That(prepared, Has.Count.EqualTo(1));
        Assert.That(catalogue.OccupiedIds.Contains(prepared[0].ArtistId), Is.False);
        Assert.That(catalogue.OccupiedIds.Contains(prepared[0].AlbumId), Is.False);
    }

    [TestCase(ArtistName)]
    [TestCase(ArtistAlias)]
    public async Task Resolves_A_Shared_Artist_Sync_Id_To_The_Receiver_Name(string artist)
    {
        using var fixture = CreateSharedGroups();
        var catalogue = await fixture.ReadAsync();
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            "Independent Album",
            artist);

        var prepared = await SourceTestData.PrepareAsync(source.Root, catalogue, output);
        Assert.That(prepared.Single().ArtistId, Is.EqualTo(SharedArtistId));
        Assert.That(prepared.Single().Metadata.AlbumArtist, Is.EqualTo(ArtistName));
    }

    [Test]
    public async Task Preserves_Byte_Identical_Tracks_When_Their_Group_Sync_Ids_Are_Shared()
    {
        using var fixture = CreateSharedGroups();
        var catalogue = await fixture.ReadAsync();
        const string hash = "matching-hash";
        var sourceTrack = new SourceTrack(
            "renamed.mp3",
            5,
            DateTime.UnixEpoch,
            hash);

        var source = new SourceLibrary("source", [sourceTrack]);
        var plan = await SyncPlanner.CreateAsync(
            "phone",
            source,
            catalogue,
            (_, _) => Task.FromResult<string?>(hash),
            CancellationToken.None);

        Assert.That(plan.Add, Is.Empty);
        Assert.That(plan.Delete, Is.Empty);
        Assert.That(plan.Matches.Single().Device.Id, Is.EqualTo(101));
    }

    [TestCase(AlbumName)]
    [TestCase("Album.")]
    public async Task Resolves_A_Shared_Album_Sync_Id_To_The_Qualified_Receiver_Name(string album)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("INSERT INTO album VALUES(13,1012,'Album.',11,'Other Sort');");
        var catalogue = await fixture.ReadAsync();
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            album,
            ArtistName);

        var prepared = await SourceTestData.PrepareAsync(source.Root, catalogue, output);
        Assert.That(prepared.Single().AlbumId, Is.EqualTo(SharedAlbumId));
        Assert.That(prepared.Single().Metadata.Album, Is.EqualTo(AlbumName));
    }

    [Test]
    public async Task Verifies_The_Actual_Group_Row_When_Sync_Ids_Are_Shared([Values] bool album)
    {
        using var fixture = CreateSharedGroups();
        var before = await fixture.ReadAsync();
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            AlbumName,
            ArtistAlias);

        var addition = (await SourceTestData.PrepareAsync(source.Root, before, output)).Single();
        var stored = FeatureTestData.Stored(addition);
        stored = stored with
        {
            Metadata = stored.Metadata! with { AlbumRowId = 12, AlbumArtistRowId = 11 }
        };

        var after = before with { Anchor = before.Anchor + 1, Tracks = [.. before.Tracks, stored] };
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [addition],
            []));

        var wrong = stored with
        {
            Metadata = album ? stored.Metadata! with { AlbumRowId = 13 }
                : stored.Metadata! with { AlbumArtistRowId = 22 }
        };

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after with { Tracks = [.. before.Tracks, wrong] },
            [addition],
            []));
    }

    [Test]
    public async Task Cancels_Group_Selection_Before_Any_Device_Write(
        [Values] bool dryRun,
        [Values] bool endOfInput)
    {
        using var fixture = CreateSharedGroups();
        fixture.Execute("UPDATE album_artist SET album_artist='Other Artist' WHERE album_artist_pid=11;");
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice
        {
            PromptForGroupName = (_, _) => ValueTask.FromResult<string?>(endOfInput ? null : string.Empty)
        };

        device.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        SourceTestData.WriteTrack(
            source.Root,
            "affected.mp3",
            AlbumName,
            ArtistAlias);

        var failure = await Assert.CatchAsync<Exception>(() => device.RunAsync(source.Root, state.Root, dryRun));
        Assert.That(failure, endOfInput ? Is.TypeOf<InvalidDataException>() : Is.TypeOf<OperationCanceledException>());
        SimulatedSyncDeviceAssertions.HasNoWrites(device);
        Assert.That(Directory.GetFiles(state.Root, "pending.json", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Rejects_Duplicate_Internal_Group_Row_Ids([Values] bool album)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(album
            ? "INSERT INTO album VALUES(12,2012,'Other Album',11,'Other Sort');"
            : "INSERT INTO album_artist VALUES(11,2011,'Other Artist','Other Sort');");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadAsync());
    }

    [TestCase("UPDATE album SET album_pid=23 WHERE album_pid=13;")]
    [TestCase("UPDATE album_artist SET album_artist_pid=23 WHERE album_artist_pid=22; "
        + "UPDATE album SET album_artist_pid=23 WHERE album_artist_pid=22;")]
    [TestCase("UPDATE album SET album_artist_pid=22 WHERE album_pid=12;")]
    [TestCase("UPDATE album SET sync_id=1014 WHERE album_pid=13;")]
    [TestCase("UPDATE album_artist SET sync_id=1014 WHERE album_artist_pid=22;")]
    public async Task Rejects_Group_Row_Changes_Hidden_By_Ambiguous_Name_Maps(string mutation)
    {
        using var fixture = CreateSharedGroups();
        fixture.Execute("""
            DELETE FROM item;
            UPDATE album_artist SET album_artist='Artist',sort_album_artist='Artist, The';
            UPDATE album SET sort_album='Album, The';
            """);

        var before = await fixture.ReadAsync();
        fixture.Execute(mutation);

        var after = await fixture.ReadAsync();
        Assert.That(after.Albums, Is.EquivalentTo(before.Albums));
        Assert.That(after.Artists, Is.EquivalentTo(before.Artists));
        Assert.That(after.OccupiedIds.SetEquals(before.OccupiedIds), Is.True);
        Assert.That(after.SortNames.Preserves(before.SortNames), Is.True);
        CatalogueAssertions.RejectsUnrequestedChanges(before, after);
    }

    [Test]
    public async Task Allows_Deletion_By_Track_Id_While_Shared_Group_Rows_Remain()
    {
        using var fixture = CreateSharedGroups();
        var before = await fixture.ReadAsync();
        fixture.Execute("DELETE FROM item WHERE item_pid=1; UPDATE _MLDatabaseProperties SET value='8';");
        var after = await fixture.ReadAsync();
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            before.Tracks));

        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
    }

    [Test]
    public async Task Allows_Collection_To_Make_A_Previously_Shared_Sync_Id_Unambiguous([Values] bool sameNames)
    {
        using var fixture = CreateSharedGroups();
        fixture.Execute("DELETE FROM item WHERE item_pid IN (2,3,4);");
        if (sameNames)
        {
            fixture.Execute("UPDATE album_artist SET album_artist='Artist' WHERE album_artist_pid=22;");
        }

        var before = await fixture.ReadAsync();
        fixture.Execute("""
            DELETE FROM item WHERE item_pid=1;
            DELETE FROM album WHERE album_pid=12;
            DELETE FROM album_artist WHERE album_artist_pid=11;
            UPDATE _MLDatabaseProperties SET value='8';
            """);

        var after = await fixture.ReadAsync();
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            before.Tracks));

        Assert.That(after.Artists.Values.Single(), Is.EqualTo(SharedArtistId));
        Assert.That(after.Albums.Values.Single(), Is.EqualTo(SharedAlbumId));
        Assert.That(after.ProtectedItems, Is.EqualTo(before.ProtectedItems));
    }

    [Test]
    public async Task Refuses_Replacement_Of_An_Unused_Group_Row_During_A_Deletion()
    {
        using var fixture = CreateSharedGroups();
        fixture.Execute("DELETE FROM item WHERE item_pid<>1;");
        var before = await fixture.ReadAsync();
        fixture.Execute("""
            DELETE FROM item WHERE item_pid=1;
            UPDATE album SET album_pid=23 WHERE album_pid=13;
            UPDATE _MLDatabaseProperties SET value='8';
            """);

        var after = await fixture.ReadAsync();
        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            before.Tracks));
    }

    [Test]
    public async Task Restores_Every_Shared_Group_Row_Through_A_Json_Checkpoint()
    {
        using var fixture = CreateSharedGroups();
        var catalogue = await fixture.ReadAsync();
        var json = JsonSerializer.Serialize(CatalogueCheckpoint.Capture(catalogue));
        var restored = JsonSerializer.Deserialize<CatalogueCheckpoint>(json)!.Restore();
        Assert.That(restored.AlbumGroups, Is.EqualTo(catalogue.AlbumGroups));
        Assert.That(restored.ArtistGroups, Is.EqualTo(catalogue.ArtistGroups));
        Assert.DoesNotThrow(() => SyncEngine.RequireUnchanged(restored, catalogue));
        fixture.Execute("UPDATE album SET album_artist_pid=11 WHERE album_pid=13;");
        var changed = await fixture.ReadAsync();
        Assert.Throws<IOException>(() => SyncEngine.RequireUnchanged(restored, changed));
    }

    [Test]
    public async Task Requires_Row_Evidence_For_Legacy_Checkpoints_Only_When_Sync_Ids_Are_Shared([Values] bool shared)
    {
        using var fixture = shared ? CreateSharedGroups() : new CatalogueFixture();
        var catalogue = await fixture.ReadAsync();
        var legacy = (CatalogueCheckpoint.Capture(catalogue) with
        {
            AlbumGroups = null,
            ArtistGroups = null
        }).Restore();

        if (shared)
        {
            Assert.Throws<IOException>(() => SyncEngine.RequireUnchanged(legacy, catalogue));
        }
        else
        {
            Assert.DoesNotThrow(() => SyncEngine.RequireUnchanged(legacy, catalogue));
        }
    }

    [Test]
    public async Task Refuses_Ambiguous_Additions_Before_Any_Device_Write_Even_In_Dry_Run([Values] bool dryRun)
    {
        using var fixture = CreateSharedGroups();
        fixture.Execute("UPDATE album_artist SET album_artist='Other Artist' WHERE album_artist_pid=11;");
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice();
        device.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            "Independent Album",
            ArtistAlias);

        await Assert.ThrowsAsync<InvalidDataException>(() => device.RunAsync(source.Root, state.Root, dryRun));
        SimulatedSyncDeviceAssertions.HasNoWrites(device);
        Assert.That(Directory.GetFiles(state.Root, "pending.json", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Previews_Changes_With_Shared_Groups_Without_Writing_Device_Or_State([Values] bool shared)
    {
        using var fixture = CreateSharedGroups();
        using var source = new SyncWorkspace();
        using var state = new SyncWorkspace();
        using var device = new SimulatedSyncDevice();
        device.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            shared ? AlbumName : "Independent Album",
            shared ? ArtistAlias : "Independent Artist");

        var result = await device.RunAsync(source.Root, state.Root, dryRun: true);
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(result.Plan.Delete.Select(track => track.Id), Is.EqualTo([101]));
        Assert.That(result.VerifiedBatches, Is.Zero);
        Assert.That(device.Batches, Is.Empty);
        Assert.That(device.FileConnections.SelectMany(connection => connection.WritePackets), Is.Empty);
        Assert.That(Directory.EnumerateFileSystemEntries(state.Root), Is.Empty);
    }

    private static CatalogueFixture CreateSharedGroups()
    {
        var fixture = new CatalogueFixture();
        fixture.Execute("""
            INSERT INTO album_artist VALUES(22,1011,'Artist.','Other Artist, The');
            INSERT INTO album VALUES(13,1012,'Album',22,'Other Album Sort');
            UPDATE item SET album_artist_pid=22,album_pid=13 WHERE item_pid=5;
            """);

        return fixture;
    }
}
