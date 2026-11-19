using System.Globalization;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncManifestTests
{
    [Test]
    public void Allows_Large_Manifests_For_New_And_Retained_Tracks(
        [Values] bool includeAdditions,
        [Values(SyncProtocol.ArtworkPart, SyncProtocol.GeniusPart)] long ancillaryParts)
    {
        const int retainedTrackCount = 1230;
        const int additionCount = 491;
        var template = SyncTestData.Track();
        var additions = includeAdditions
            ? Enumerable.Range(0, additionCount).Select(index => template with { Id = template.Id - index }).ToArray()
            : [];

        var retained = Enumerable.Range(1, retainedTrackCount).Select(id => (long)id).ToHashSet();
        var entries = additions.Select(track => (object)SyncTestData.Asset(track.AssetId))
            .Concat(retained.Select(id => SyncTestData.Asset(id.ToString(CultureInfo.InvariantCulture), ancillaryParts)))
            .ToArray();

        var result = SyncManifest.Validate(SyncTestData.Manifest(entries), additions, retained);

        Assert.That(result, Has.Count.EqualTo(additions.Length + retained.Count));
        Assert.That(result.Take(additions.Length).Select(asset => asset.Track), Is.EqualTo(additions));
        Assert.That(result.Skip(additions.Length).All(asset => asset.Track is null), Is.True);
    }

    [Test]
    public void Rejects_Unknown_And_Duplicate_Ids_In_Large_Manifests([Values] bool duplicate)
    {
        const int retainedTrackCount = 1230;
        const long firstRetainedId = 1;
        var track = SyncTestData.Track();
        var retained = Enumerable.Range((int)firstRetainedId, retainedTrackCount).Select(id => (long)id).ToHashSet();
        var entries = retained.Select(id => (object)SyncTestData.Asset(
            id.ToString(CultureInfo.InvariantCulture), SyncProtocol.ArtworkPart)).Prepend(SyncTestData.Asset(track.AssetId))
            .ToArray();

        var invalidId = duplicate ? firstRetainedId : retainedTrackCount + firstRetainedId;
        entries[^1] = SyncTestData.Asset(invalidId.ToString(CultureInfo.InvariantCulture), SyncProtocol.ArtworkPart);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SyncManifest.Validate(SyncTestData.Manifest(entries), [track], retained));

        Assert.That(exception!.Message, Does.Contain(duplicate ? "duplicate" : "Unexpected asset request"));
    }

    [Test]
    public void Limits_Entries_To_Distinct_New_And_Retained_Track_Ids([Values] bool artworkOnly)
    {
        const long retainedId = 101;
        var track = artworkOnly ? FeatureTestData.Covered(artworkOnly: true) : SyncTestData.Track();
        var retained = new HashSet<long> { retainedId };
        if (artworkOnly)
        {
            retained.Add(track.Id);
        }

        var own = SyncTestData.Asset(track.AssetId, artworkOnly ? SyncProtocol.ArtworkPart : SyncProtocol.AudioPart);
        var ancillary = SyncTestData.Asset(retainedId.ToString(CultureInfo.InvariantCulture), SyncProtocol.GeniusPart);
        var result = SyncManifest.Validate(SyncTestData.Manifest(own, ancillary), [track], retained);
        Assert.That(result, Has.Count.EqualTo(2));

        var exception = Assert.Throws<InvalidDataException>(() =>
            SyncManifest.Validate(SyncTestData.Manifest(own, ancillary, ancillary), [track], retained));

        Assert.That(exception!.Message, Does.Contain("contains 3 entries").And.Contain("only 2 distinct track IDs"));
    }

    [Test]
    public void Reports_Unsupported_Manifest_Keys()
    {
        var parameters = SyncTestData.Manifest();
        parameters.Dictionary(AtcKeys.AssetManifest)[AtcKeys.Book] = Array.Empty<object>();

        var exception = Assert.Throws<InvalidDataException>(() =>
            SyncManifest.Validate(parameters, [], new HashSet<long>()));

        Assert.That(exception!.Message, Does.Contain("keys:").And.Contain(AtcKeys.Book).And.Contain(AtcKeys.Media));
    }

    [TestCase(null, "missing or null")]
    [TestCase("not an array", "String")]
    [TestCase(1L, "Int64")]
    public void Reports_Missing_Or_Unexpected_Media_Types(object? media, string expectedType)
    {
        var manifest = Plist.Dict();
        if (media is not null)
        {
            manifest[AtcKeys.Media] = media;
        }

        var parameters = Plist.Dict((AtcKeys.AssetManifest, manifest));
        var exception = Assert.Throws<InvalidDataException>(() =>
            SyncManifest.Validate(parameters, [], new HashSet<long>()));

        Assert.That(exception!.Message, Does.Contain(AtcKeys.Media).And.Contain("array").And.Contain(expectedType));
    }

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
