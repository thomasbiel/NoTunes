namespace NoTunes.Music;

internal static class CatalogueVerification
{
    private const string MetadataMismatchMessage = "The iPhone track metadata differs after synchronization.";

    public static void VerifyResult(
        DeviceCatalogue before,
        DeviceCatalogue after,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlyList<DeviceTrack> deletions)
    {
        RequireNextRevision(before, after);
        RequireProtectedItems(before, after);

        var deletedIds = deletions.Select(track => track.Id).ToHashSet();
        RequireExpectedInventory(
            before,
            after,
            additions,
            deletedIds);

        RequireRetainedArtwork(
            before,
            after,
            additions,
            deletedIds);

        if (!PreservesGroups(
            before,
            after,
            allowRemovedGroups: deletions.Count != 0,
            allowNewGroups: additions.Any(track => !track.IsArtworkOnly)))
        {
            throw new IOException(MetadataMismatchMessage);
        }

        var actualTracks = after.Tracks.ToDictionary(track => track.Id);
        RequireRetainedTracks(before.Tracks, actualTracks, deletedIds);
        foreach (var track in additions.Where(track => !track.IsArtworkOnly))
        {
            if (!MatchesAddition(actualTracks[track.Id], track, after))
            {
                throw new IOException(MetadataMismatchMessage);
            }
        }
    }

    public static void RequireUnchanged(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        if (!HasSameLibraryState(expected, actual) ||
            !HasSameTrackState(expected, actual) ||
            !HasSameArtworkState(expected, actual) ||
            !HasSameGroupState(expected, actual))
        {
            throw new IOException("The iPhone catalogue changed after planning. Run the sync again.");
        }
    }

    private static void RequireNextRevision(DeviceCatalogue before, DeviceCatalogue after)
    {
        if (!HasSameIdentity(before, after) || after.Anchor != checked(before.Anchor + 1))
        {
            throw new IOException("The iPhone did not commit the expected library revision.");
        }
    }

    private static void RequireProtectedItems(DeviceCatalogue before, DeviceCatalogue after)
    {
        if (!before.ProtectedItems.SequenceEqual(after.ProtectedItems))
        {
            throw new IOException("Protected cloud, remote or nonmusic items changed during synchronization.");
        }
    }

    private static void RequireExpectedInventory(
        DeviceCatalogue before,
        DeviceCatalogue after,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlySet<long> deletedIds)
    {
        var expectedIds = before.Tracks
            .Select(track => track.Id)
            .Where(id => !deletedIds.Contains(id))
            .Concat(additions.Where(track => !track.IsArtworkOnly).Select(track => track.Id))
            .ToHashSet();

        if (!expectedIds.SetEquals(after.Tracks.Select(track => track.Id)))
        {
            throw new IOException("The iPhone track inventory differs from the requested changes.");
        }
    }

    private static void RequireRetainedArtwork(
        DeviceCatalogue before,
        DeviceCatalogue after,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlySet<long> deletedIds)
    {
        var updatedIds = additions.Where(track => track.IsArtworkOnly).Select(track => track.Id).ToHashSet();
        foreach (var (id, expected) in before.TrackArtwork)
        {
            if (deletedIds.Contains(id) || updatedIds.Contains(id)) continue;

            if (!after.TrackArtwork.TryGetValue(id, out var actual) || actual != expected)
            {
                throw new IOException("Artwork on an unchanged track differs after synchronization.");
            }
        }
    }

    private static void RequireRetainedTracks(
        IReadOnlyList<DeviceTrack> before,
        IReadOnlyDictionary<long, DeviceTrack> actual,
        IReadOnlySet<long> deletedIds)
    {
        foreach (var track in before)
        {
            if (!deletedIds.Contains(track.Id) && actual[track.Id] != track)
            {
                throw new IOException(MetadataMismatchMessage);
            }
        }
    }

    private static bool MatchesAddition(DeviceTrack actual, PreparedTrack expected, DeviceCatalogue catalogue)
    {
        var sameAudio = actual.Path == expected.DevicePath && actual.Size == expected.Source.Size;
        var sameTitle = actual.Title == (string)expected.Metadata.Item[MediaKeys.Title];
        if (!sameAudio || !sameTitle) return false;

        if (actual.Metadata is not { } metadata || !metadata.Matches(expected)) return false;

        var albumKey = CatalogueReader.GroupKey(expected.Metadata.Album, expected.Metadata.AlbumArtist);
        var sameAlbum = catalogue.Albums.TryGetValue(albumKey, out var albumId) && albumId == expected.AlbumId;
        var sameArtist = catalogue.Artists.TryGetValue(expected.Metadata.AlbumArtist, out var artistId) && artistId == expected.ArtistId;
        return sameAlbum && sameArtist;
    }

    private static bool HasSameIdentity(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        return expected.DatabaseId == actual.DatabaseId && expected.ContainerId == actual.ContainerId;
    }

    private static bool HasSameLibraryState(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        return HasSameIdentity(expected, actual)
            && expected.Anchor == actual.Anchor
            && expected.OccupiedIds.SetEquals(actual.OccupiedIds);
    }

    private static bool HasSameTrackState(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        return expected.Tracks.SequenceEqual(actual.Tracks)
            && expected.ProtectedItems.SequenceEqual(actual.ProtectedItems);
    }

    private static bool HasSameArtworkState(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        if (expected.ArtworkHighWaterMark != actual.ArtworkHighWaterMark
            || expected.TrackArtwork.Count != actual.TrackArtwork.Count) return false;

        foreach (var (id, expectedArtwork) in expected.TrackArtwork)
        {
            if (!actual.TrackArtwork.TryGetValue(id, out var artwork) || artwork != expectedArtwork) return false;
        }

        return true;
    }

    private static bool HasSameGroupState(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        var sameGroupCounts = expected.Albums.Count == actual.Albums.Count && expected.Artists.Count == actual.Artists.Count;
        return sameGroupCounts
            && PreservesGroups(expected, actual, allowRemovedGroups: false)
            && expected.SortNames.Preserves(actual.SortNames);
    }

    private static bool PreservesGroups(
        DeviceCatalogue before,
        DeviceCatalogue after,
        bool allowRemovedGroups,
        bool allowNewGroups = false)
    {
        var completeRows = before.AlbumGroups is not null && before.ArtistGroups is not null
            && after.AlbumGroups is not null && after.ArtistGroups is not null;
        // Name maps classify insertion targets; collection can change that classification without changing a row.
        var preservesNames = completeRows
            ? after.SortNames.PreservesItemGroups(before.SortNames, allowRemovedGroups)
            : after.SortNames.Preserves(before.SortNames, allowRemovedGroups)
                && PreservesGroupIds(before.Albums, after.Albums, allowRemovedGroups)
                && PreservesGroupIds(before.Artists, after.Artists, allowRemovedGroups);
        return preservesNames
            && PreservesGroupRows(
                before.AlbumGroups,
                after.AlbumGroups,
                allowRemovedGroups,
                allowNewGroups)
            && PreservesGroupRows(
                before.ArtistGroups,
                after.ArtistGroups,
                allowRemovedGroups,
                allowNewGroups);
    }

    private static bool PreservesGroupRows(
        IReadOnlyList<CatalogueGroup>? expected,
        IReadOnlyList<CatalogueGroup>? actual,
        bool allowRemovedGroups,
        bool allowNewGroups)
    {
        if (expected is null)
        {
            // Older checkpoints predate row snapshots and could not contain shared sync IDs.
            return actual is null || actual.Where(group => group.SyncId != 0).GroupBy(group => group.SyncId)
                .All(group => group.Count() == 1);
        }

        if (actual is null || !allowRemovedGroups && !allowNewGroups && expected.Count != actual.Count) return false;

        var rows = actual.ToDictionary(group => group.RowId);
        if (!allowNewGroups)
        {
            var expectedIds = expected.Select(group => group.RowId).ToHashSet();
            if (rows.Keys.Any(id => !expectedIds.Contains(id))) return false;
        }

        return expected.All(group => rows.TryGetValue(group.RowId, out var row)
            ? group.Matches(row) : allowRemovedGroups);
    }

    private static bool PreservesGroupIds(
        IReadOnlyDictionary<string, long> expected,
        IReadOnlyDictionary<string, long> actual,
        bool allowRemovedGroups)
    {
        return expected.All(pair => actual.TryGetValue(pair.Key, out var id) ? id == pair.Value : allowRemovedGroups);
    }
}
