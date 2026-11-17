using NoTunes.Apple;

namespace NoTunes.Music;

internal static class SyncOperations
{
    private const long DefaultAlbumFieldOrder = 0;
    private const long DefaultPlayOrder = 1;
    private const long RepeatOff = 0;

    public static byte[] Initialize(long database, long container, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (database == 0 || container == 0 || database == container)
            throw new InvalidDataException("Initialization requires distinct nonzero identities.");

        var created = DateTime.UtcNow;
        created = created.AddTicks(-(created.Ticks % TimeSpan.TicksPerSecond));
        return BinaryPlist.Write(Plist.Dict(
            (MediaKeys.Revision, SyncProtocol.FirstMediaRevision),
            (MediaKeys.Timestamp, created),
            (MediaKeys.Operations, new object[]
            {
                Plist.Dict((MediaKeys.Operation, MediaOperations.UpdateDatabase), (MediaKeys.PersistentId, database),
                    (MediaKeys.DatabaseInfo, Plist.Dict((MediaKeys.PrimaryContainerId, container)))),
                Plist.Dict(
                    (MediaKeys.Operation, MediaOperations.InsertPlaylist),
                    (MediaKeys.PersistentId, container),
                    (MediaKeys.Container, Plist.Dict(
                        (MediaKeys.Name, name),
                        (MediaKeys.IsHidden, true),
                        (MediaKeys.DateCreated, created),
                        (MediaKeys.DateModified, created))),
                    (MediaKeys.ContainerUi, Plist.Dict(
                        (MediaKeys.AlbumFieldOrder, DefaultAlbumFieldOrder),
                        (MediaKeys.HasBeenShuffled, false),
                        (MediaKeys.IsReversed, false),
                        (MediaKeys.PlayOrder, DefaultPlayOrder),
                        (MediaKeys.RepeatMode, RepeatOff),
                        (MediaKeys.ShuffleItems, false))),
                    (MediaKeys.ItemToContainer, Array.Empty<object>()))
            })));
    }

    public static byte[] Serialize(DeviceCatalogue catalogue, IReadOnlyList<PreparedTrack> additions, IReadOnlyList<DeviceTrack> deletions)
    {
        return BinaryPlist.Write(CreateDocument(catalogue, additions, deletions));
    }

    internal static byte[]? SerializeWithinLimit(
        DeviceCatalogue catalogue,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlyList<DeviceTrack> deletions)
    {
        return BinaryPlist.WriteWithinLimit(CreateDocument(catalogue, additions, deletions), SyncProtocol.MaxOperationBytes);
    }

    private static Dictionary<string, object> CreateDocument(
        DeviceCatalogue catalogue,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlyList<DeviceTrack> deletions)
    {
        if (additions.Count + deletions.Count is < 1 or > SyncProtocol.MaxTrackChanges)
            throw new InvalidDataException($"A sync batch must contain between 1 and {SyncProtocol.MaxTrackChanges} track changes.");

        var operations = new List<object>
        {
            Plist.Dict((MediaKeys.Operation, MediaOperations.UpdateDatabase), (MediaKeys.PersistentId, catalogue.DatabaseId),
                (MediaKeys.DatabaseInfo, Plist.Dict((MediaKeys.PrimaryContainerId, catalogue.ContainerId))))
        };
        foreach (var track in additions)
        {
            if (track.IsArtworkOnly)
            {
                var artwork = track.Metadata.Artwork ?? throw new InvalidDataException("An artwork update has no image.");
                operations.Add(Plist.Dict(
                    (MediaKeys.Operation, MediaOperations.UpdateTrack),
                    (MediaKeys.PersistentId, track.Id),
                    (MediaKeys.Item, Plist.Dict((MediaKeys.ArtworkCacheId, (long)artwork.CacheId),
                        (MediaKeys.ArtworkChecksum, (long)artwork.Checksum)))));
                continue;
            }

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

        return Plist.Dict(
            (MediaKeys.Revision, (long)checked(catalogue.Anchor + 1)),
            (MediaKeys.Timestamp, DateTime.UtcNow),
            (MediaKeys.Operations, operations.ToArray()));
    }
}
