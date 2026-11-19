using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncRecoveryGroupTests
{
    private const long NewArtistRowId = 22;
    private const long NewAlbumRowId = 23;
    private const string NewArtist = "New Artist";
    private const string NewAlbum = "New Album";
    private const long ReboundAlbumId = 2012;

    [Test]
    public void Restores_Legacy_Name_Maps_Without_Choosing_An_Ambiguous_Ascii_Case_Variant()
    {
        var snapshot = CatalogueCheckpoint.Capture(CatalogueTestData.Create([])) with
        {
            Artists = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["Die Ärzte"] = 1011,
                ["DIe Ärzte"] = 2011
            },
            SortNames = GroupSortNames.Empty with
            {
                AlbumArtists = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Die Ärzte"] = "First Sort",
                    ["DIe Ärzte"] = "Second Sort"
                }
            }
        };

        var restored = snapshot.Restore();
        Assert.That(restored.Artists, Has.Count.EqualTo(1));
        Assert.That(restored.Artists["die Ärzte"], Is.Zero);
        Assert.That(restored.SortNames.AlbumArtists["die Ärzte"], Is.Null);
        Assert.That(snapshot.Artists, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Reconciles_Groups_Created_By_Interrupted_Insertions([Values] bool audioTransferred)
    {
        var (pending, after, addition) = await CreateScenarioAsync(audioTransferred);
        var incomplete = SyncRecovery.Assess(pending, after, [addition]);
        Assert.That(incomplete.Select(track => track.Id), Is.EqualTo(audioTransferred ? [] : new[] { addition.Id }));
    }

    [TestCase("album")]
    [TestCase("artist")]
    public async Task Still_Rejects_Changes_To_Preexisting_Groups_During_Insertion_Recovery(string group)
    {
        var (pending, after, addition) = await CreateScenarioAsync(audioTransferred: false);
        var changed = group == "album"
            ? after with
            {
                AlbumGroups = [.. after.AlbumGroups!.Select(row => row.RowId == NewAlbumRowId
                    ? row : row with { SortName = "Unexpected sort name" })]
            }
            : after with
            {
                ArtistGroups = [.. after.ArtistGroups!.Select(row => row.RowId == NewArtistRowId
                    ? row : row with { SortName = "Unexpected sort name" })]
            };

        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, changed, [addition]));
    }

    [Test]
    public async Task Recovers_A_Recorded_Rebinding_Of_The_Same_Album_Row()
    {
        var (pending, after, addition) = await CreateRebindingScenarioAsync();
        var operation = pending.Operation.ToArray();
        Assert.That(SyncRecovery.Assess(pending, after, [addition]).Single().Id, Is.EqualTo(addition.Id));
        Assert.That(pending.Before.AlbumGroups!.Single().SyncId, Is.EqualTo(1012));
        Assert.That(pending.Operation, Is.EqualTo(operation));
    }

    [TestCase("identity")]
    [TestCase("name")]
    [TestCase("sort")]
    [TestCase("membership")]
    public async Task Rejects_Unrecorded_Changes_To_A_Rebound_Album(string mutation)
    {
        var (pending, after, addition) = await CreateRebindingScenarioAsync();
        var changed = after.AlbumGroups!.Single();
        changed = mutation switch
        {
            "identity" => changed with { SyncId = ReboundAlbumId + 1 },
            "name" => changed with { Name = "Unrelated Album" },
            "sort" => changed with { SortName = "Unexpected Sort" },
            "membership" => changed with { AlbumArtistRowId = NewArtistRowId },
            _ => throw new AssertionException("Unknown mutation.")
        };

        Assert.Throws<IOException>(() =>
            SyncRecovery.Assess(pending, after with { AlbumGroups = [changed] }, [addition]));
    }

    [Test]
    public async Task Rejects_Unrelated_Metadata_Changes_On_A_Track_Referencing_A_Rebound_Group()
    {
        var (pending, after, addition) = await CreateRebindingScenarioAsync();
        var retained = after.Tracks.Single(track => track.Id != addition.Id);
        var changed = retained with { Metadata = retained.Metadata! with { TrackNumber = 99 } };
        after = after with { Tracks = [changed, .. after.Tracks.Where(track => track.Id == addition.Id)] };
        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, after, [addition]));
    }

    [Test]
    public async Task Rejects_New_Groups_Not_Backed_By_The_Pending_Operation()
    {
        var (pending, after, addition) = await CreateScenarioAsync(audioTransferred: false);
        var foreign = new CatalogueGroup(
            24,
            2024,
            "Foreign Artist",
            "Foreign Artist",
            null,
            null);

        after = after with { ArtistGroups = [.. after.ArtistGroups!, foreign] };
        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, after, [addition]));
    }

    [Test]
    public async Task Requires_Recorded_Track_Membership_For_A_Receiver_Artist_Alias([Values] bool associated)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("UPDATE album_artist SET album_artist='Céline Dion',sort_album_artist='Céline Dion';");
        var before = await fixture.ReadAsync();
        var addition = SyncTestData.Track();
        var item = new Dictionary<string, object>(addition.Metadata.Item)
        {
            [MediaKeys.Album] = NewAlbum,
            [MediaKeys.AlbumArtist] = "Celine Dion"
        };

        addition = addition with
        {
            AlbumId = ReboundAlbumId,
            ArtistId = 2011,
            Metadata = addition.Metadata with { Item = item, Album = NewAlbum, AlbumArtist = "Celine Dion" }
        };

        fixture.Execute($"""
            INSERT INTO album VALUES({NewAlbumRowId},{ReboundAlbumId},'{NewAlbum}',11,'{NewAlbum}');
            UPDATE _MLDatabaseProperties SET value='8';
            """);

        var stored = FeatureTestData.Stored(addition) with
        {
            Path = null,
            Size = 0,
            Metadata = associated ? new TrackMetadata { AlbumRowId = NewAlbumRowId, AlbumArtistRowId = 11 } : null
        };

        var after = await fixture.ReadAsync();
        after = after with { Tracks = [.. after.Tracks, stored] };
        var pending = CreatePending(before, addition);
        if (associated)
        {
            Assert.That(SyncRecovery.Assess(pending, after, [addition]).Single().Id, Is.EqualTo(addition.Id));
        }
        else
        {
            Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, after, [addition]));
        }
    }

    private static async Task<(PendingSync Pending, DeviceCatalogue After, PreparedTrack Addition)>
        CreateRebindingScenarioAsync()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("""
            DELETE FROM item WHERE item_pid<>1;
            UPDATE album_artist SET album_artist='Die Ärzte',sort_album_artist='Die Ärzte';
            UPDATE item_artist SET item_artist='Die Ärzte',sort_item_artist='Die Ärzte';
            """);

        var before = await fixture.ReadAsync();
        var addition = SyncTestData.Track();
        var item = new Dictionary<string, object>(addition.Metadata.Item)
        {
            [MediaKeys.Album] = "Album",
            [MediaKeys.AlbumArtist] = "DIe Ärzte"
        };

        addition = addition with
        {
            AlbumId = ReboundAlbumId,
            ArtistId = 2011,
            Metadata = addition.Metadata with { Item = item, Album = "Album", AlbumArtist = "DIe Ärzte" }
        };

        fixture.Execute($"UPDATE album SET sync_id={ReboundAlbumId}; UPDATE _MLDatabaseProperties SET value='8';");
        var after = await fixture.ReadAsync();
        var incomplete = new DeviceTrack(
            addition.Id,
            (string)addition.Metadata.Item[MediaKeys.Title],
            null,
            0);

        after = after with { Tracks = [.. after.Tracks, incomplete] };
        return (CreatePending(before, addition), after, addition);
    }

    private static async Task<(PendingSync Pending, DeviceCatalogue After, PreparedTrack Addition)> CreateScenarioAsync(
        bool audioTransferred)
    {
        using var fixture = new CatalogueFixture();
        var before = await fixture.ReadAsync();
        using var source = new SyncWorkspace();
        using var workspace = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "new.mp3",
            NewAlbum,
            NewArtist);

        var addition = (await SourceTestData.PrepareAsync(source.Root, before, workspace)).Single();
        fixture.Execute($"""
            INSERT INTO album_artist VALUES({NewArtistRowId},{addition.ArtistId},'{NewArtist}','{NewArtist}');
            INSERT INTO album VALUES({NewAlbumRowId},{addition.AlbumId},'{NewAlbum}',{NewArtistRowId},'{NewAlbum}');
            UPDATE _MLDatabaseProperties SET value='8';
            """);

        var stored = FeatureTestData.Stored(addition);
        stored = stored with
        {
            Path = audioTransferred ? stored.Path : null,
            Size = audioTransferred ? stored.Size : 0,
            Metadata = stored.Metadata! with { AlbumRowId = NewAlbumRowId, AlbumArtistRowId = NewArtistRowId }
        };

        var after = await fixture.ReadAsync();
        after = after with { Tracks = [.. after.Tracks, stored] };
        return (CreatePending(before, addition), after, addition);
    }

    private static PendingSync CreatePending(DeviceCatalogue before, PreparedTrack addition)
    {
        return new PendingSync(
            2,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [JournalTrack.Capture(addition)],
            [],
            SyncOperations.Serialize(before, [addition], []),
            [1, 2]);
    }
}
