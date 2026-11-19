namespace NoTunes.Music;

internal static class RecoveryGroupProjection
{
    internal static DeviceCatalogue Create(
        DeviceCatalogue before,
        DeviceCatalogue actual,
        IReadOnlyList<PreparedTrack> additions)
    {
        var targets = additions.Where(track => !track.IsArtworkOnly)
            .Select(track => GroupAliasResolution.ForVerification(track, before)).ToArray();

        if (targets.Length == 0) return before;

        var tracks = actual.Tracks.ToDictionary(track => track.Id);
        var albums = ProjectRows(
            before.AlbumGroups,
            actual.AlbumGroups,
            targets,
            before.OccupiedIds,
            tracks,
            album: true);

        var artists = ProjectRows(
            before.ArtistGroups,
            actual.ArtistGroups,
            targets,
            before.OccupiedIds,
            tracks,
            album: false);

        var albumBindings = ChangedBindings(before.AlbumGroups, albums);
        var artistBindings = ChangedBindings(before.ArtistGroups, artists);
        return before with
        {
            AlbumGroups = albums,
            ArtistGroups = artists,
            Tracks = [.. before.Tracks.Select(track => ProjectTrack(track, albumBindings, artistBindings))]
        };
    }

    private static IReadOnlyList<CatalogueGroup>? ProjectRows(
        IReadOnlyList<CatalogueGroup>? before,
        IReadOnlyList<CatalogueGroup>? after,
        IReadOnlyList<PreparedTrack> targets,
        IReadOnlySet<long> occupied,
        IReadOnlyDictionary<long, DeviceTrack> tracks,
        bool album)
    {
        if (before is null || after is null) return before;

        var actualRows = after.ToDictionary(row => row.RowId);
        var expectedIds = before.Select(row => row.RowId).ToHashSet();
        var result = new List<CatalogueGroup>(before.Count);
        foreach (var previous in before)
        {
            if (actualRows.TryGetValue(previous.RowId, out var actual) && actual.SyncId != previous.SyncId)
            {
                var sameRow = (previous with { SyncId = actual.SyncId }).Matches(actual);
                if (!sameRow || occupied.Contains(actual.SyncId)
                    || !targets.Any(track => MatchesTarget(
                        actual,
                        track,
                        tracks,
                        album)))
                {
                    throw new IOException($"Recovery found an unrecorded change to "
                        + $"{(album ? "album" : "album artist")} row {previous.RowId}.");
                }

                // Only the derived sync ID changed; row identity, membership, names and sort values remain protected.
                result.Add(actual);
            }
            else
            {
                result.Add(previous);
            }
        }

        foreach (var created in after.Where(row => !expectedIds.Contains(row.RowId)))
        {
            if (!targets.Any(track => MatchesTarget(
                created,
                track,
                tracks,
                album)))
            {
                throw new IOException($"Recovery found an unrecorded new "
                    + $"{(album ? "album" : "album artist")} row {created.RowId}.");
            }

            result.Add(created);
        }

        return result;
    }

    private static bool MatchesTarget(
        CatalogueGroup row,
        PreparedTrack track,
        IReadOnlyDictionary<long, DeviceTrack> tracks,
        bool album)
    {
        var exact = album
            ? row.SyncId == track.AlbumId && GroupNameComparer.Instance.Equals(row.Name, track.Metadata.Album)
                && GroupNameComparer.Instance.Equals(row.AlbumArtist, track.Metadata.AlbumArtist)
            : row.SyncId == track.ArtistId && GroupNameComparer.Instance.Equals(row.Name, track.Metadata.AlbumArtist);

        if (exact) return true;

        // Legacy requests may have used a distinct spelling for an existing receiver group.
        // Accept that mapping only when the recorded insertion actually points at this internal row.
        if (!tracks.TryGetValue(track.Id, out var stored) || stored.Metadata is not { } metadata) return false;

        return album
            ? row.SyncId == track.AlbumId && metadata.AlbumRowId == row.RowId
                && metadata.AlbumArtistRowId == row.AlbumArtistRowId
                && ReceiverGroupName.Matches(row.Name, track.Metadata.Album)
                && ReceiverGroupName.Matches(row.AlbumArtist, track.Metadata.AlbumArtist)
            : row.SyncId == track.ArtistId && metadata.AlbumArtistRowId == row.RowId
                && ReceiverGroupName.Matches(row.Name, track.Metadata.AlbumArtist);
    }

    private static Dictionary<long, (long Before, long After)> ChangedBindings(
        IReadOnlyList<CatalogueGroup>? before,
        IReadOnlyList<CatalogueGroup>? projected)
    {
        if (before is null || projected is null) return [];

        var rows = projected.ToDictionary(row => row.RowId);
        return before.Where(row => rows[row.RowId].SyncId != row.SyncId)
            .ToDictionary(row => row.RowId, row => (row.SyncId, rows[row.RowId].SyncId));
    }

    private static DeviceTrack ProjectTrack(
        DeviceTrack track,
        IReadOnlyDictionary<long, (long Before, long After)> albums,
        IReadOnlyDictionary<long, (long Before, long After)> artists)
    {
        if (track.Metadata is not { } metadata) return track;

        var albumId = metadata.AlbumRowId is { } albumRow && albums.TryGetValue(albumRow, out var album)
            && metadata.AlbumId == album.Before ? album.After : metadata.AlbumId;

        var artistId = metadata.AlbumArtistRowId is { } artistRow && artists.TryGetValue(artistRow, out var artist)
            && metadata.AlbumArtistId == artist.Before ? artist.After : metadata.AlbumArtistId;

        return track with { Metadata = metadata with { AlbumId = albumId, AlbumArtistId = artistId } };
    }
}
