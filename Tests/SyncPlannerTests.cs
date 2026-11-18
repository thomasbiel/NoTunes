using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncPlannerTests
{
    private const long DefaultTrackId = 101;
    private const long DefaultTrackSize = 5;

    [Test]
    public async Task Counts_Unmatched_Sizes_Incomplete_And_Missing_Tracks_In_Progress()
    {
        await using var error = new StringWriter();
        var reads = 0;
        var result = await CreatePlanAsync(
            (track, _) =>
            {
                reads++;
                return Task.FromResult(track.Id == 104 ? "A" : null);
            },
            source: [Track("new.mp3", "A")],
            device:
            [
                StoredTrack(size: 10),
                StoredTrack(102, path: null),
                StoredTrack(103, "missing.mp3"),
                StoredTrack(104, "match.mp3")
            ],
            error: error);

        Assert.That(reads, Is.EqualTo(2));
        Assert.That(result.Unchanged, Is.EqualTo(1));
        Assert.That(error.ToString(),
            Does.Contain("25% (1/4)").And.Contain("50% (2/4)").And.Contain("75% (3/4)").And.Contain("100% (4/4)"));
    }

    [Test]
    public async Task Does_Not_Report_Completion_When_Reading_Fails()
    {
        await using var error = new StringWriter();
        await Assert.ThrowsAsync<IOException>(() => CreatePlanAsync(
            (_, _) => throw new IOException("Read failed"), error: error));

        Assert.That(error.ToString(), Does.Contain("0% (0/1)").And.Not.Contain("100%"));
    }

    [Test]
    public async Task Rejects_An_Already_Cancelled_Plan_With_An_Empty_Device_Catalogue([Values] bool hasSource)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CreatePlanAsync(
            (_, _) => throw new AssertionException("No device read was expected."),
            source: hasSource ? [Track("new.mp3", "A")] : [],
            device: [],
            cancellationToken: cancellation.Token));
    }

    [Test]
    public async Task Stops_When_Cancelled_After_The_Last_Device_Hash()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CreatePlanAsync(
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<string?>("A");
            },
            source: [Track("new.mp3", "A")],
            cancellationToken: cancellation.Token));
    }

    [Test]
    public async Task Preserves_Renamed_Tracks_And_Matches_Duplicate_Content_One_To_One()
    {
        var result = await CreatePlanAsync(
            (track, _) => Task.FromResult<string?>(track.Id == DefaultTrackId ? "A" : "C"),
            source: [Track("new-name.mp3", "A"), Track("duplicate.mp3", "A"), Track("new.mp3", "B")],
            device: [StoredTrack(), StoredTrack(102, "F01/extra.mp3")]);

        Assert.That(result.Unchanged, Is.EqualTo(1));
        Assert.That(result.Add.Select(track => track.RelativePath), Is.EquivalentTo(["duplicate.mp3", "new.mp3"]));
        Assert.That(result.Delete.Select(track => track.Id), Is.EqualTo([102L]));
    }

    [Test]
    public async Task Does_Not_Read_Assets_With_No_Possible_Size_Match()
    {
        var result = await CreatePlanAsync(
            (_, _) => throw new AssertionException("Unneeded device read"),
            device: [StoredTrack(size: 10)]);

        AssertReplacement(result);
    }

    [Test]
    public async Task An_Unreadable_Device_Asset_Aborts_The_Plan()
    {
        await Assert.ThrowsAsync<IOException>(() => CreatePlanAsync((_, _) => throw new IOException("Denied")));
    }

    [Test]
    public async Task A_Missing_Device_File_Is_Replaced()
    {
        var result = await CreatePlanAsync((_, _) => Task.FromResult<string?>(null));
        AssertReplacement(result);
    }

    private static Task<SyncPlan> CreatePlanAsync(
        Func<DeviceTrack, CancellationToken, Task<string?>> readHash,
        IReadOnlyList<SourceTrack>? source = null,
        IReadOnlyList<DeviceTrack>? device = null,
        CancellationToken cancellationToken = default,
        TextWriter? error = null)
    {
        return SyncPlanner.CreateAsync(
            "phone",
            new SourceLibrary("source", source ?? [Track("new.mp3", "B")]),
            CatalogueTestData.Create(device ?? [StoredTrack()]),
            readHash,
            cancellationToken,
            error);
    }

    private static SourceTrack Track(string name, string hash)
    {
        return new SourceTrack(
            name,
            DefaultTrackSize,
            DateTime.UnixEpoch,
            hash);
    }

    private static DeviceTrack StoredTrack(
        long id = DefaultTrackId,
        string? path = "F01/old.mp3",
        long size = DefaultTrackSize)
    {
        return new DeviceTrack(
            id,
            "old",
            path,
            size);
    }

    private static void AssertReplacement(SyncPlan plan)
    {
        Assert.That(plan.Add.Select(track => track.RelativePath), Is.EqualTo(["new.mp3"]));
        Assert.That(plan.Delete.Select(track => track.Id), Is.EqualTo([DefaultTrackId]));
    }
}
