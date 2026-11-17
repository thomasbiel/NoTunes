using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncCatalogueGuardTests
{
    private const string SwapAlbumAndComposerIds =
        "UPDATE album SET sync_id=1014; UPDATE composer SET sync_id=1012;";
    private const string SwapArtistAndComposerIds =
        "UPDATE album_artist SET sync_id=1014; UPDATE composer SET sync_id=1011;";

    [TestCase(SwapAlbumAndComposerIds)]
    [TestCase(SwapArtistAndComposerIds)]
    public async Task Rejects_Reassigned_Group_Ids_Before_A_Batch(string mutation)
    {
        using var fixture = new CatalogueFixture();
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        fixture.Execute(mutation);
        var changed = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(changed.Tracks.Select(track => track.Id), Is.EqualTo(before.Tracks.Select(track => track.Id)));
        Assert.That(changed.OccupiedIds.SetEquals(before.OccupiedIds), Is.True);
        Assert.Throws<IOException>(() => SyncEngine.RequireUnchanged(before, changed));
    }

    [TestCase(SwapAlbumAndComposerIds)]
    [TestCase(SwapArtistAndComposerIds)]
    public async Task Rejects_Reassigned_Group_Ids_After_A_Batch(string mutation)
    {
        using var fixture = new CatalogueFixture();
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        fixture.Execute(mutation + "UPDATE _MLDatabaseProperties SET value='8';");
        var after = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            []));
    }

    [Test]
    public async Task Accepts_Ascii_Group_Name_Variants_With_Unchanged_Ids()
    {
        using var fixture = new CatalogueFixture();
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        fixture.Execute("UPDATE album SET album='ALBUM'; UPDATE album_artist SET album_artist='ARTIST';");
        var changed = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.DoesNotThrow(() => SyncEngine.RequireUnchanged(before, changed));
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            changed with { Anchor = before.Anchor + 1 },
            [],
            []));
    }
}
