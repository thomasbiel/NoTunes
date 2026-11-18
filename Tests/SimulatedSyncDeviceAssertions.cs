using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

internal static class SimulatedSyncDeviceAssertions
{
    public static void HasNoWrites(SimulatedSyncDevice device)
    {
        Assert.That(device.Batches, Is.Empty);
        Assert.That(device.FileConnections.SelectMany(connection => connection.WritePackets), Is.Empty);
        Assert.That(device.Files.ContainsKey(SyncPaths.Lock), Is.False);
    }

    public static void HasArtworkFrom(
        SimulatedSyncDevice device,
        DeviceCatalogue catalogue,
        long trackId,
        string sourcePath)
    {
        var artwork = catalogue.TrackArtwork[trackId];
        Assert.That(artwork.Path, Is.Not.Null);
        var expected = Mp3Metadata.Read(sourcePath, Path.GetFileName(sourcePath)).Artwork!.Bytes;
        Assert.That(device.Files[SyncPaths.ArtworkOriginals + artwork.Path], Is.EqualTo(expected));
    }
}
