using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncBatchTests
{
    [Test]
    public void Splits_Large_Metadata_Into_Signable_Batches_Without_Losing_Changes()
    {
        const int trackCount = 499;
        const int textLength = 4096;
        var template = SyncTestData.Track();
        var metadata = template.Metadata with { Item = new Dictionary<string, object>(template.Metadata.Item) };
        metadata.Item[MediaKeys.Title] = new string('t', textLength);
        metadata.Item[MediaKeys.SortTitle] = new string('t', textLength);
        metadata.Item[MediaKeys.Comment] = new string('c', textLength);
        var tracks = Enumerable.Range(0, trackCount)
            .Select(index => template with { Id = template.Id - index, Metadata = metadata }).ToArray();
        var catalogue = SyncPlannerTests.Catalogue([]);
        Assert.That(SyncOperations.Serialize(catalogue, tracks, []).Length, Is.GreaterThan(SyncProtocol.MaxOperationBytes));
        var received = new List<long>();
        var batches = 0;
        while (received.Count < tracks.Length)
        {
            var batch = SyncBatch.Create(catalogue, tracks.Skip(received.Count).ToArray(), []);
            Assert.That(batch.Operation.Length, Is.LessThanOrEqualTo(SyncProtocol.MaxOperationBytes));
            using var signer = new GrappaSigner(GrappaSignerTests.Reply());
            Assert.That(signer.Sign(batch.Operation), Has.Length.EqualTo(21));
            var document = BinaryPlist.Read(batch.Operation).AsDict();
            Assert.That(document.Number(MediaKeys.Revision), Is.EqualTo(catalogue.Anchor + 1));
            var operations = (object[])document[MediaKeys.Operations];
            received.AddRange(operations.Skip(1).Select(operation => operation.AsDict().Number(MediaKeys.PersistentId)));
            catalogue = catalogue with { Anchor = catalogue.Anchor + 1 };
            batches++;
        }

        Assert.That(batches, Is.GreaterThan(1));
        Assert.That(received, Is.EqualTo(tracks.Select(track => track.Id)));
    }

    [Test]
    public void Respects_The_Track_Count_Limit_For_Deletions()
    {
        var tracks = Enumerable.Range(0, SyncProtocol.MaxTrackChanges + 1)
            .Select(index => new DeviceTrack(
                index,
                "Track",
                "track.mp3",
                5)).ToArray();
        var batch = SyncBatch.Create(SyncPlannerTests.Catalogue(tracks), [], tracks);
        Assert.That(batch.Deletions, Has.Count.EqualTo(SyncProtocol.MaxTrackChanges));
        Assert.That(batch.Deletions.Select(track => track.Id), Is.EqualTo(tracks.Take(SyncProtocol.MaxTrackChanges)
            .Select(track => track.Id)));
    }

    [Test]
    public void Rejects_A_Single_Change_That_Cannot_Be_Staged()
    {
        var track = SyncTestData.Track();
        track.Metadata.Item[MediaKeys.Comment] = new string('c', SyncProtocol.MaxOperationBytes);
        Assert.Throws<InvalidDataException>(() => SyncBatch.Create(SyncPlannerTests.Catalogue([]), [track], []));
    }
}
