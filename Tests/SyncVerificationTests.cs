using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncVerificationTests
{
    [TestCase(null, 101U)]
    [TestCase("aa/other.jpg", 101U)]
    [TestCase("aa/cover.jpg", 102U)]
    public void Rejects_Unrequested_Artwork_Changes_On_Retained_Tracks(string? path, uint cacheId)
    {
        var retained = new DeviceTrack(
            2001,
            "Retained",
            "retained.mp3",
            5);

        var removed = new DeviceTrack(
            2002,
            "Removed",
            "removed.mp3",
            5);

        var original = new ArtworkReference(101, "aa/cover.jpg");
        var before = CatalogueTestData.Create([retained, removed]) with
        {
            TrackArtwork = new Dictionary<long, ArtworkReference> { [retained.Id] = original }
        };

        var after = before with
        {
            Anchor = before.Anchor + 1,
            Tracks = [retained],
            TrackArtwork = new Dictionary<long, ArtworkReference>()
        };

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            [removed]));

        after = after with
        {
            TrackArtwork = new Dictionary<long, ArtworkReference>
            {
                [retained.Id] = new ArtworkReference(cacheId, path)
            }
        };

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            [removed]));

        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after with { TrackArtwork = before.TrackArtwork },
            [],
            [removed]));
    }

    [Test]
    public void Allows_Requested_Artwork_Updates_And_Artwork_Removal_For_Deleted_Tracks()
    {
        var repair = FeatureTestData.Covered(artworkOnly: true);
        var retained = FeatureTestData.Stored(repair);
        var removed = new DeviceTrack(
            2002,
            "Removed",
            "removed.mp3",
            5);

        var before = CatalogueTestData.Create([retained, removed]) with
        {
            TrackArtwork = new Dictionary<long, ArtworkReference>
            {
                [retained.Id] = new ArtworkReference(100, null),
                [removed.Id] = new ArtworkReference(99, "aa/removed.jpg")
            }
        };

        var after = before with
        {
            Anchor = before.Anchor + 1,
            Tracks = [retained],
            TrackArtwork = new Dictionary<long, ArtworkReference>
            {
                [retained.Id] = new ArtworkReference(repair.Metadata.Artwork!.CacheId, "aa/repaired.jpg")
            }
        };

        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [repair],
            [removed]));
    }

    [Test]
    public async Task Accepts_Collected_Groups_After_Verifying_The_Deleted_Track_Inventory()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("DELETE FROM item WHERE item_pid<>1;");
        var (before, after) = await fixture.ReadTransitionAsync("""
            DELETE FROM item WHERE item_pid=1;
            DELETE FROM item_store WHERE item_pid=1;
            DELETE FROM item_extra WHERE item_pid=1;
            DELETE FROM album;
            DELETE FROM album_artist;
            DELETE FROM item_artist;
            DELETE FROM composer;
            UPDATE _MLDatabaseProperties SET value='8';
            """);

        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            before.Tracks));

        Assert.That(after.SortNames.Preserves(before.SortNames), Is.False,
            "The catalogue comparison before a batch must still reject missing groups.");

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            []));
    }

    [Test]
    public void Rejects_Changes_To_Existing_Group_Sort_Names()
    {
        var before = CatalogueTestData.Create([]) with
        {
            SortNames = GroupSortNames.Empty with
            {
                Artists = new Dictionary<string, string?> { ["Artist"] = "Artist, The" }
            }
        };

        var after = before with
        {
            Anchor = before.Anchor + 1,
            SortNames = before.SortNames with
            {
                Artists = new Dictionary<string, string?> { ["Artist"] = "Artist" }
            }
        };

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            []));
    }

    [Test]
    public void Accepts_Only_The_Expected_Track_Transition_And_Next_Anchor()
    {
        var before = CatalogueTestData.Create([new(
            101,
            "Old",
            "old.mp3",
            5), new(
                102,
                "Keep",
                "keep.mp3",
                6)]);

        var track = SyncTestData.Track();
        var added = FeatureTestData.Stored(track);
        var after = before with
        {
            Anchor = before.Anchor + 1,
            Tracks = [before.Tracks[1], added],
            Albums = new Dictionary<string, long>
            {
                [CatalogueReader.GroupKey(track.Metadata.Album, track.Metadata.AlbumArtist)] = track.AlbumId
            },
            Artists = new Dictionary<string, long> { [track.Metadata.AlbumArtist] = track.ArtistId }
        };

        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [track],
            [before.Tracks[0]]));

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after with { Anchor = before.Anchor },
            [track],
            [before.Tracks[0]]));

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after with { Tracks = [added] },
            [track],
            [before.Tracks[0]]));

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after with { DatabaseId = 99 },
            [track],
            [before.Tracks[0]]));

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after with { Tracks = [before.Tracks[1], added with { Path = "wrong.mp3" }] },
            [track],
            [before.Tracks[0]]));
    }
}
