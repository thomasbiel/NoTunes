using NoTunes.Apple;

namespace NoTunes.Music;

internal static class SyncOperations
{
    public static byte[] Serialize(DeviceCatalogue catalogue, IReadOnlyList<PreparedTrack> additions, IReadOnlyList<DeviceTrack> deletions)
    {
        if (additions.Count + deletions.Count is < 1 or > SyncProtocol.MaxTrackChanges)
            throw new InvalidDataException("A sync batch must contain between 1 and 499 track changes.");

        var operations = new List<object>
        {
            Plist.Dict((MediaKeys.Operation, MediaOperations.UpdateDatabase), (MediaKeys.PersistentId, catalogue.DatabaseId),
                (MediaKeys.DatabaseInfo, Plist.Dict((MediaKeys.PrimaryContainerId, catalogue.ContainerId))))
        };
        foreach (var track in additions)
        {
            var item = new Dictionary<string, object>(track.Metadata.Item)
            {
                [MediaKeys.AlbumId] = track.AlbumId,
                [MediaKeys.ArtistId] = track.ArtistId
            };
            operations.Add(Plist.Dict(
                (MediaKeys.Operation, MediaOperations.InsertTrack),
                (MediaKeys.PersistentId, track.Id),
                (MediaKeys.Item, item),
                (MediaKeys.Audio, track.Metadata.Audio),
                (MediaKeys.Location, Plist.Dict((MediaKeys.Kind, Mp3Metadata.LocationKind))),
                (MediaKeys.Stats, Plist.Dict(
                    (MediaKeys.UserPlayCount, 0L),
                    (MediaKeys.RecentPlayCount, 0L),
                    (MediaKeys.UserSkipCount, 0L),
                    (MediaKeys.RecentSkipCount, 0L),
                    (MediaKeys.HasBeenPlayed, false)))));
        }

        foreach (var track in deletions)
        {
            operations.Add(Plist.Dict((MediaKeys.Operation, MediaOperations.DeleteTrack), (MediaKeys.PersistentId, track.Id)));
        }

        return BinaryPlist.Write(Plist.Dict(
            (MediaKeys.Revision, (long)checked(catalogue.Anchor + 1)),
            (MediaKeys.Timestamp, DateTime.UtcNow),
            (MediaKeys.Operations, operations.ToArray())));
    }
}
