namespace NoTunes.Music;

internal static class GroupAliasResolution
{
    private const string AliasPunctuation = ".";

    public static CatalogueGroup? Resolve(IReadOnlyList<CatalogueGroup>? rows, string key)
    {
        if (rows is null) return null;

        var requested = rows.Where(row => GroupNameComparer.Instance.Equals(row.Key, key)).Take(2).ToArray();
        if (requested.Length != 1 || requested[0].SyncId == 0) return null;

        // Shared sync-ID lookups on the receiver select the first internal row.
        var selected = rows.Where(row => row.SyncId == requested[0].SyncId).MinBy(row => row.RowId)!;
        var selectedName = selected.Name.Replace(AliasPunctuation, string.Empty, StringComparison.Ordinal);
        var requestedName = requested[0].Name.Replace(AliasPunctuation, string.Empty, StringComparison.Ordinal);
        var sameName = GroupNameComparer.Instance.Equals(selected.Name, requested[0].Name)
            || selectedName.Length != 0 && GroupNameComparer.Instance.Equals(selectedName, requestedName);

        if (!sameName || selected.AlbumArtistRowId != requested[0].AlbumArtistRowId) return null;

        if (rows.Count(row => GroupNameComparer.Instance.Equals(row.Key, selected.Key)) != 1) return null;

        return selected;
    }

    public static PreparedTrack ForVerification(PreparedTrack track, DeviceCatalogue before)
    {
        var artist = Resolve(before.ArtistGroups, track.Metadata.AlbumArtist);
        var album = Resolve(before.AlbumGroups, CatalogueReader.GroupKey(track.Metadata.Album, track.Metadata.AlbumArtist));
        var useArtist = artist is not null && artist.SyncId == track.ArtistId
            && !GroupNameComparer.Instance.Equals(artist.Name, track.Metadata.AlbumArtist);

        var artistName = useArtist ? artist!.Name : track.Metadata.AlbumArtist;
        var useAlbum = album is not null && album.SyncId == track.AlbumId
            && GroupNameComparer.Instance.Equals(album.AlbumArtist, artistName)
            && !GroupNameComparer.Instance.Equals(album.Name, track.Metadata.Album);

        if (!useArtist && !useAlbum) return track;

        // Project the known receiver spelling; the signed operation and journal remain untouched.
        var item = new Dictionary<string, object>(track.Metadata.Item);
        if (useArtist)
        {
            item[MediaKeys.AlbumArtist] = artist!.Name;
            item[MediaKeys.SortAlbumArtist] = artist.SortName;
        }

        if (useAlbum)
        {
            item[MediaKeys.Album] = album!.Name;
            item[MediaKeys.SortAlbum] = album.SortName;
        }

        return track with
        {
            Metadata = track.Metadata with
            {
                Item = item,
                AlbumArtist = artistName,
                Album = useAlbum ? album!.Name : track.Metadata.Album
            }
        };
    }
}
