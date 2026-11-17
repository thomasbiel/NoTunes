using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncPlannerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Rejects_An_Already_Cancelled_Plan_With_An_Empty_Device_Catalogue(bool hasSource)
    {
        var source = new SourceLibrary("source", hasSource ? [Track("new.mp3", "A")] : []);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SyncPlanner.CreateAsync(
            "phone",
            source,
            Catalogue([]),
            (_, _) => throw new AssertionException("No device read was expected."),
            cancellation.Token));
    }

    [Test]
    public async Task Stops_When_Cancelled_After_The_Last_Device_Hash()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SyncPlanner.CreateAsync(
            "phone",
            new SourceLibrary("source", [Track("new.mp3", "A")]),
            Catalogue([new(
                101,
                "old",
                "F01/old.mp3",
                5)]),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<string?>("A");
            },
            cancellation.Token));
    }

    [Test]
    public async Task Preserves_Renamed_Tracks_And_Matches_Duplicate_Content_One_To_One()
    {
        SourceTrack[] source = [Track("new-name.mp3", "A"), Track("duplicate.mp3", "A"), Track("new.mp3", "B")];
        var catalogue = Catalogue([new(
            101,
            "renamed",
            "F01/old.mp3",
            5), new(
                102,
                "extra",
                "F01/extra.mp3",
                5)]);
        var result = await SyncPlanner.CreateAsync(
            "phone",
            new SourceLibrary("source", source),
            catalogue,
            (track, _) => Task.FromResult<string?>(track.Id == 101 ? "A" : "C"),
            CancellationToken.None);
        Assert.That(result.Unchanged, Is.EqualTo(1));
        Assert.That(result.Add.Select(track => track.RelativePath), Is.EquivalentTo(["duplicate.mp3", "new.mp3"]));
        Assert.That(result.Delete.Select(track => track.Id), Is.EqualTo([102L]));
    }

    [Test]
    public async Task Does_Not_Read_Assets_With_No_Possible_Size_Match()
    {
        var catalogue = Catalogue([new(
            101,
            "old",
            "F01/old.mp3",
            10)]);
        var result = await SyncPlanner.CreateAsync(
            "phone",
            new SourceLibrary("source", [Track("new.mp3", "B")]),
            catalogue,
            (_, _) => throw new AssertionException("Unneeded device read"),
            CancellationToken.None);
        Assert.That(result.Add, Has.Count.EqualTo(1));
        Assert.That(result.Delete, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task An_Unreadable_Device_Asset_Aborts_The_Plan()
    {
        await Assert.ThrowsAsync<IOException>(() => SyncPlanner.CreateAsync(
            "phone",
            new SourceLibrary("source", [Track("new.mp3", "B")]),
            Catalogue([new(
                101,
                "old",
                "F01/old.mp3",
                5)]),
            (_, _) => throw new IOException("Denied"),
            CancellationToken.None));
    }

    [Test]
    public async Task A_Missing_Device_File_Is_Replaced()
    {
        var result = await SyncPlanner.CreateAsync(
            "phone",
            new SourceLibrary("source", [Track("new.mp3", "B")]),
            Catalogue([new(
                101,
                "old",
                "F01/old.mp3",
                5)]),
            (_, _) => Task.FromResult<string?>(null),
            CancellationToken.None);
        Assert.That(result.Add, Has.Count.EqualTo(1));
        Assert.That(result.Delete, Has.Count.EqualTo(1));
    }

    internal static DeviceCatalogue Catalogue(IReadOnlyList<DeviceTrack> tracks)
    {
        return new DeviceCatalogue(
            1001,
            1002,
            7,
            tracks,
            new Dictionary<string, long>(),
            new Dictionary<string, long>(),
            tracks.Select(track => track.Id).Append(1001).Append(1002).ToHashSet());
    }

    private static SourceTrack Track(string name, string hash)
    {
        return new(
            name,
            5,
            DateTime.UnixEpoch,
            hash);
    }
}
