using NoTunes.Music;

namespace NoTunes.Tests;

internal static class CatalogueTestData
{
    private const long DatabaseId = 1001;
    private const long ContainerId = 1002;
    private const uint Anchor = 7;

    public static DeviceCatalogue Create(IReadOnlyList<DeviceTrack> tracks)
    {
        return new DeviceCatalogue(
            DatabaseId,
            ContainerId,
            Anchor,
            tracks,
            new Dictionary<string, long>(),
            new Dictionary<string, long>(),
            tracks.Select(track => track.Id).Append(DatabaseId).Append(ContainerId).ToHashSet());
    }
}
