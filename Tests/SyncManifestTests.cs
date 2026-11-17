using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncManifestTests
{
    [Test]
    public void Allows_Only_Expected_Audio_And_Known_Ancillary_Requests()
    {
        var track = SyncTestData.Track();
        var result = SyncManifest.Validate(SyncTestData.Manifest(
            SyncTestData.Asset(track.AssetId), SyncTestData.Asset("101", SyncProtocol.ArtworkPart)), [track], new HashSet<long> { 101 });
        Assert.That(result[0].Track, Is.SameAs(track));
        Assert.That(result[1].Track, Is.Null);
    }

    [TestCase(SyncProtocol.ArtworkPart)]
    [TestCase(SyncProtocol.AudioPart | SyncProtocol.ArtworkPart)]
    [TestCase(0L)]
    public void Rejects_Unexpected_Parts_For_New_Audio(long parts)
    {
        var track = SyncTestData.Track();
        Assert.Throws<InvalidDataException>(() => SyncManifest.Validate(SyncTestData.Manifest(SyncTestData.Asset(track.AssetId, parts)),
            [track], new HashSet<long>()));
    }

    [Test]
    public void Rejects_Missing_Fields_And_Duplicate_Or_Missing_New_Assets()
    {
        var track = SyncTestData.Track();
        Assert.Throws<InvalidDataException>(() => SyncManifest.Validate(Plist.Dict(), [], new HashSet<long>()));
        Assert.Throws<InvalidDataException>(() => SyncManifest.Validate(SyncTestData.Manifest(), [track], new HashSet<long>()));
        Assert.Throws<InvalidDataException>(() => SyncManifest.Validate(SyncTestData.Manifest(
            SyncTestData.Asset(track.AssetId), SyncTestData.Asset(track.AssetId)), [track], new HashSet<long>()));
    }
}
