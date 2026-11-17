using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CatalogueLimitTests
{
    [Test]
    public async Task Reads_The_Maximum_Music_Inventory_With_Excluded_Items_After_It()
    {
        using var fixture = new CatalogueFixture();
        fixture.ReplaceLocalTracks(CatalogueReader.MaximumTracks);
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(before.Tracks, Has.Count.EqualTo(CatalogueReader.MaximumTracks));
        fixture.Execute("""
            INSERT INTO item(item_pid,media_type,remote_location_id,base_location_id)
            VALUES(50001,2,0,1),(50002,8,1,1);
            INSERT INTO item_store VALUES(50001,150001,0),(50002,150002,0);
            INSERT INTO item_extra(item_pid,title,location,file_size)
            VALUES(50001,'Video','video.mp4',5),(50002,'Remote','remote.mp3',5);
            """);
        var after = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
    }

    [Test]
    public async Task Rejects_Music_Above_The_Catalogue_Limit()
    {
        using var fixture = new CatalogueFixture();
        fixture.ReplaceLocalTracks(CatalogueReader.MaximumTracks + 1);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => CatalogueReader.ReadAsync(
            fixture.Snapshot(), CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("track limit"));
    }
}
