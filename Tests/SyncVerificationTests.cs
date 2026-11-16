using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SyncVerificationTests
{
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
            track.Source.Size);
        var after = before with { Anchor = before.Anchor + 1, Tracks = [before.Tracks[1], added] };
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
