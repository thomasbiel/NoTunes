using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncVerificationTests
{
    [Test]
    public async Task Accepts_Collected_Groups_After_Verifying_The_Deleted_Track_Inventory()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("DELETE FROM item WHERE item_pid<>1;");
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        fixture.Execute("""
            DELETE FROM item WHERE item_pid=1;
            DELETE FROM item_store WHERE item_pid=1;
            DELETE FROM item_extra WHERE item_pid=1;
            DELETE FROM album;
            DELETE FROM album_artist;
            DELETE FROM item_artist;
            DELETE FROM composer;
            UPDATE _MLDatabaseProperties SET value='8';
            """);
        var after = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
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
        var before = SyncPlannerTests.Catalogue([]) with
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
        var before = SyncPlannerTests.Catalogue([new(
            101,
            "Old",
            "old.mp3",
            5), new(
                102,
                "Keep",
                "keep.mp3",
                6)]);
        var track = SyncTestData.Track();
        var added = new DeviceTrack(
            track.Id,
            (string)track.Metadata.Item[MediaKeys.Title],
            track.DevicePath,
            track.Source.Size)
        {
            Metadata = new TrackMetadata
            {
                AlbumId = track.AlbumId,
                AlbumArtistId = track.ArtistId,
                Album = track.Metadata.Album,
                AlbumArtist = track.Metadata.AlbumArtist,
                Artist = (string)track.Metadata.Item[MediaKeys.Artist],
                Composer = track.Metadata.Item.GetValueOrDefault(MediaKeys.Composer) as string,
                Genre = (string)track.Metadata.Item[MediaKeys.Genre],
                SortTitle = (string)track.Metadata.Item[MediaKeys.SortTitle],
                SortAlbum = (string)track.Metadata.Item[MediaKeys.SortAlbum],
                SortAlbumArtist = (string)track.Metadata.Item[MediaKeys.SortAlbumArtist],
                SortArtist = (string)track.Metadata.Item[MediaKeys.SortArtist],
                SortComposer = track.Metadata.Item.GetValueOrDefault(MediaKeys.SortComposer) as string,
                Comment = track.Metadata.Item.GetValueOrDefault(MediaKeys.Comment) as string,
                TrackNumber = track.Metadata.Item.GetValueOrDefault(MediaKeys.TrackNumber) as long?,
                TrackCount = track.Metadata.Item.GetValueOrDefault(MediaKeys.TrackCount) as long?,
                DiscNumber = track.Metadata.Item.GetValueOrDefault(MediaKeys.DiscNumber) as long? ?? 1,
                DiscCount = track.Metadata.Item.GetValueOrDefault(MediaKeys.DiscCount) as long?,
                Year = track.Metadata.Item.GetValueOrDefault(MediaKeys.Year) as long?,
                Compilation = track.Metadata.Item.GetValueOrDefault(MediaKeys.IsCompilation) is true ? 1 : 0,
                TotalMilliseconds = Math.Floor((long)track.Metadata.Audio[MediaKeys.Duration] * 1000.0
                    / (double)track.Metadata.Audio[MediaKeys.SampleRate]),
                MediaKind = 1,
                AudioFormat = (long)track.Metadata.Audio[MediaKeys.AudioFormat],
                BitRate = (long)track.Metadata.Audio[MediaKeys.BitRate],
                SampleRate = (double)track.Metadata.Audio[MediaKeys.SampleRate],
                Duration = (long)track.Metadata.Audio[MediaKeys.Duration],
                RememberBookmark = 0
            }
        };
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
