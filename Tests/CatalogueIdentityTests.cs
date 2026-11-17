using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CatalogueIdentityTests
{
    [Test]
    public async Task Rejects_An_Initialized_Library_Without_Its_Primary_Container()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("UPDATE container SET container_pid=2002 WHERE sync_id=1002;");
        var exception = await Assert.ThrowsAsync<IOException>(() => CatalogueReader.ReadAsync(
            fixture.Snapshot(), CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("primary container is missing"));
    }

    [TestCase(0)]
    [TestCase(2002)]
    public async Task Resolves_The_Primary_Container_By_Its_Row_Identity(int syncId)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute($"UPDATE container SET sync_id={syncId} WHERE container_pid=1002;");
        var catalogue = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(catalogue.ContainerId, Is.EqualTo(1002));
        Assert.That(catalogue.OccupiedIds.Contains(0), Is.False);
        if (syncId != 0)
        {
            Assert.That(catalogue.OccupiedIds.Contains(syncId), Is.True);
        }
    }

    [TestCase("UPDATE item_store SET sync_id=101 WHERE item_pid=3;")]
    [TestCase("UPDATE item_store SET sync_id=101 WHERE item_pid=4;")]
    [TestCase("UPDATE item_store SET sync_id=101 WHERE item_pid=5;")]
    [TestCase("UPDATE item_store SET sync_id=103 WHERE item_pid=1;")]
    [TestCase("UPDATE item_store SET sync_id=105 WHERE item_pid=3;")]
    public async Task Rejects_Duplicate_Track_Ids_Including_Protected_Items(string mutation)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => CatalogueReader.ReadAsync(
            fixture.Snapshot(), CancellationToken.None));
    }

    [TestCase("INSERT INTO album VALUES(13,1012,'Other album',11,'Other sort');")]
    [TestCase("INSERT INTO album VALUES(13,1012,'Album',11,'Album, The');")]
    [TestCase("INSERT INTO album_artist VALUES(22,1011,'Other artist','Other sort');")]
    [TestCase("INSERT INTO album_artist VALUES(22,1011,'Artist','Artist, The');")]
    public async Task Rejects_Duplicate_Group_Ids_Even_When_Labels_Agree(string mutation)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => CatalogueReader.ReadAsync(
            fixture.Snapshot(), CancellationToken.None));
    }

    [Test]
    public async Task Allows_Several_Protected_Items_Without_A_Sync_Identity()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("UPDATE item_store SET sync_id=0 WHERE item_pid IN (3,4,5);");
        var result = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(result.ProtectedItems, Has.Count.EqualTo(4));
        fixture.Execute("DELETE FROM item_store WHERE item_pid IN (3,4,5);");
        result = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(result.ProtectedItems, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task Allows_Groups_Without_Sync_Identities()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("""
            INSERT INTO album_artist VALUES(22,0,'Other artist','Other artist'),(23,0,'Third artist','Third artist');
            INSERT INTO album VALUES(24,0,'Other album',22,'Other album'),(25,0,'Third album',23,'Third album');
            """);
        var result = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(result.Artists.Values, Is.EqualTo(new long[] { 1011 }));
        Assert.That(result.Albums.Values, Is.EqualTo(new long[] { 1012 }));
    }
}
