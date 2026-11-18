using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ProtectedCatalogueTests
{
    [TestCase("DELETE FROM item WHERE item_pid=2;")]
    [TestCase("DELETE FROM item WHERE item_pid=3;")]
    [TestCase("DELETE FROM item WHERE item_pid=4;")]
    [TestCase("DELETE FROM item WHERE item_pid=5;")]
    [TestCase("UPDATE item_extra SET title='Changed' WHERE item_pid=2;")]
    [TestCase("UPDATE item_extra SET location='changed.mp4' WHERE item_pid=3;")]
    [TestCase("UPDATE item_extra SET file_size=6 WHERE item_pid=4;")]
    [TestCase("UPDATE item SET remote_location_id=2 WHERE item_pid=4;")]
    [TestCase("UPDATE item_store SET store_saga_id=556 WHERE item_pid=5;")]
    [TestCase("UPDATE item_store SET sync_id=106 WHERE item_pid=5;")]
    [TestCase("DELETE FROM item_store WHERE item_pid=2;")]
    [TestCase("UPDATE item SET media_type=4 WHERE item_pid=3;")]
    [TestCase("INSERT INTO item(item_pid,media_type,remote_location_id,base_location_id) VALUES(6,8,1,0);")]
    public async Task Rejects_Unrequested_Changes_To_Protected_Items_After_A_Batch(string mutation)
    {
        using var fixture = new CatalogueFixture();
        var before = await fixture.ReadAsync();
        fixture.Execute("""
            DELETE FROM item WHERE item_pid=1;
            DELETE FROM item_store WHERE item_pid=1;
            DELETE FROM item_extra WHERE item_pid=1;
            UPDATE _MLDatabaseProperties SET value='8';
            """);

        fixture.Execute(mutation);
        var after = await fixture.ReadAsync();
        var exception = Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [],
            before.Tracks));

        Assert.That(exception!.Message, Does.Contain("Protected"));
    }

    [TestCase("UPDATE item_extra SET title='Changed' WHERE item_pid=2;")]
    [TestCase("DELETE FROM item WHERE item_pid=2;")]
    public async Task Rejects_Protected_Item_Changes_Before_A_Batch_Without_Changed_Sync_Ids(string mutation)
    {
        using var fixture = new CatalogueFixture();
        var (before, after) = await fixture.ReadTransitionAsync(mutation);
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(after.OccupiedIds.SetEquals(before.OccupiedIds), Is.True);
        Assert.Throws<IOException>(() => SyncEngine.RequireUnchanged(before, after));
    }

    [Test]
    public async Task Retains_Protected_Purchases_Without_Complete_Location_Or_Sync_Metadata([Values] bool missingStore)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("""
            UPDATE item SET base_location_id=0 WHERE item_pid=2;
            UPDATE item_extra SET title=NULL,location=NULL,file_size=NULL WHERE item_pid=2;
            """);

        if (missingStore)
        {
            fixture.Execute("DELETE FROM item_store WHERE item_pid=2;");
        }

        var before = await fixture.ReadAsync();
        Assert.That(before.ProtectedItems.Select(item => item.ItemId), Is.EquivalentTo(new long[] { 2, 3, 4, 5 }));
        var purchase = before.ProtectedItems.Single(item => item.ItemId == 2);
        Assert.That(purchase.SyncId, Is.EqualTo(missingStore ? (long?)null : 0L));
        Assert.That(purchase.Title, Is.Null);
        Assert.That(purchase.BasePath, Is.Null);
        Assert.That(purchase.Location, Is.Null);
        Assert.That(purchase.Size, Is.Null);
        var after = await fixture.ReadAsync();
        CatalogueAssertions.AcceptsUnchanged(before, after);
    }
}
