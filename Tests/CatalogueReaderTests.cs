using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class CatalogueReaderTests
{
    [Test]
    public async Task Keeps_Unrelated_Ambiguous_Groups_Readable()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("INSERT INTO album VALUES(13,1013,'Album',11,'Other sort')");
        var result = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        var key = CatalogueReader.GroupKey("Album", "Artist");
        Assert.That(result.Albums[key], Is.Zero);
        Assert.That(result.SortNames.Albums[key], Is.Null);
        Assert.That(result.Tracks, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Uses_The_Initial_Anchor_For_A_Legacy_Library_Without_An_Atc_Anchor()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("DELETE FROM _MLDatabaseProperties WHERE key='MLSyncClientSyncAnchor'");
        var result = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(result.Anchor, Is.Zero);
        Assert.That(result.Tracks, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Reads_Wire_Identities_And_Preserves_Cloud_Remote_And_Video_Items()
    {
        using var fixture = new CatalogueFixture();
        var result = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(result.Anchor, Is.EqualTo(7));
        Assert.That(result.DatabaseId, Is.EqualTo(1001));
        Assert.That(result.Tracks.Select(track => track.Id), Is.EqualTo([101L]));
        Assert.That(result.Tracks[0].Path, Is.EqualTo("iTunes_Control/Music/F01/one.mp3"));
        Assert.That(result.Albums[CatalogueReader.GroupKey("Album", "Artist")], Is.EqualTo(1012));
        Assert.That(result.Artists["Artist"], Is.EqualTo(1011));
        Assert.That(result.OccupiedIds, Does.Contain(105L));
    }

    [Test]
    public async Task Reads_Committed_Wal_Changes_Without_Modifying_The_Source_Capture()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("PRAGMA journal_mode=WAL; UPDATE item_extra SET title='From WAL' WHERE item_pid=1;");
        var snapshot = fixture.Snapshot();
        var original = snapshot.Database.ToArray();
        var result = await CatalogueReader.ReadAsync(snapshot, CancellationToken.None);
        Assert.That(snapshot.Wal, Is.Not.Null);
        Assert.That(result.Tracks[0].Title, Is.EqualTo("From WAL"));
        Assert.That(snapshot.Database, Is.EqualTo(original));
    }

    [TestCase("INSERT INTO db_info VALUES(2,3)")]
    [TestCase("UPDATE _MLDatabaseProperties SET value='-1'")]
    [TestCase("UPDATE item_extra SET location='../escape.mp3' WHERE item_pid=1")]
    [TestCase("PRAGMA user_version=1")]
    public void Rejects_Ambiguous_Or_Unsupported_Catalogues(string mutation)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(mutation);
        Assert.ThrowsAsync<InvalidDataException>(() => CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None));
    }
}
