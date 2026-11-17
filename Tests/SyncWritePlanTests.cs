using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncWritePlanTests
{
    private const int FirstTrackId = 10000;
    private const int MaximumMusicTracks = 50000;

    [TestCase(MaximumMusicTracks, 1, 1)]
    [TestCase(MaximumMusicTracks - 1, 2, 2)]
    [TestCase(0, MaximumMusicTracks + 1, 0)]
    public void Rejects_Plans_Whose_Additions_Exceed_The_Catalogue_Limit(int existing, int added, int deleted)
    {
        var (source, plan, catalogue) = CreatePlan(existing, added, deleted);
        Assert.Throws<IOException>(() => SyncEngine.ValidateWritePlan(
            source,
            plan,
            catalogue,
            allowEmptySource: true));
    }

    [TestCase(MaximumMusicTracks - 1, 1, 1)]
    [TestCase(MaximumMusicTracks, 0, 1)]
    [TestCase(0, MaximumMusicTracks, 0)]
    public void Allows_Plans_Whose_Intermediate_Inventory_Fits_The_Catalogue_Limit(int existing, int added, int deleted)
    {
        var (source, plan, catalogue) = CreatePlan(existing, added, deleted);
        Assert.DoesNotThrow(() => SyncEngine.ValidateWritePlan(
            source,
            plan,
            catalogue,
            allowEmptySource: true));
    }

    private static (SourceLibrary Source, SyncPlan Plan, DeviceCatalogue Catalogue) CreatePlan(
        int existing,
        int added,
        int deleted)
    {
        var catalogue = SyncPlannerTests.Catalogue([
            .. Enumerable.Range(FirstTrackId, existing)
                .Select(id => new DeviceTrack(
                    id,
                    "Existing",
                    $"F01/{id}.mp3",
                    1))
        ]);
        var additions = Enumerable.Range(0, added)
            .Select(id => new SourceTrack(
                $"{id}.mp3",
                1,
                DateTime.UnixEpoch,
                "hash")).ToArray();
        var unchanged = existing - deleted;
        var source = new SourceLibrary("source", [
            .. additions,
            .. Enumerable.Repeat(
                new SourceTrack(
                    "retained.mp3",
                    1,
                    DateTime.UnixEpoch,
                    "hash"), unchanged)
        ]);
        var plan = new SyncPlan(
            "phone",
            catalogue.Anchor,
            additions,
            [.. catalogue.Tracks.Take(deleted)],
            unchanged);
        return (source, plan, catalogue);
    }
}
