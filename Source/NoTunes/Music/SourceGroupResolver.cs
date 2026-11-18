namespace NoTunes.Music;

internal sealed class SourceGroupResolver
{
    private readonly MusicGroupResolver artists;
    private readonly MusicGroupResolver albums;
    private readonly MusicGroupResolver performers;
    private readonly MusicGroupResolver composers;

    public SourceGroupResolver(DeviceCatalogue catalogue, ISet<long> occupied, GroupNamePrompt? prompt)
    {
        this.artists = Create(
            MusicGroupKind.AlbumArtist,
            catalogue.ArtistGroups,
            catalogue.Artists,
            catalogue.SortNames.AlbumArtists);

        this.albums = Create(
            MusicGroupKind.Album,
            catalogue.AlbumGroups,
            catalogue.Albums,
            catalogue.SortNames.Albums);

        this.performers = Create(
            MusicGroupKind.Artist,
            null,
            null,
            catalogue.SortNames.Artists);

        this.composers = Create(
            MusicGroupKind.Composer,
            null,
            null,
            catalogue.SortNames.Composers);

        MusicGroupResolver Create(
            MusicGroupKind kind,
            IReadOnlyList<CatalogueGroup>? rows,
            IReadOnlyDictionary<string, long>? existingIds,
            IReadOnlyDictionary<string, string?> sorts)
        {
            return new MusicGroupResolver(
                kind,
                rows,
                existingIds,
                sorts,
                occupied,
                prompt);
        }
    }

    public async Task<ResolvedTrackGroups> ResolveAsync(
        Mp3Metadata metadata,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var artist = await this.artists.ResolveAsync(
            metadata.AlbumArtist,
            (string)metadata.Item[MediaKeys.SortAlbumArtist],
            sourcePath,
            null,
            cancellationToken);

        var album = await this.albums.ResolveAsync(
            metadata.Album,
            (string)metadata.Item[MediaKeys.SortAlbum],
            sourcePath,
            artist.Name,
            cancellationToken);

        await ResolveItemAsync(
            this.performers,
            metadata.Item,
            MediaKeys.Artist,
            MediaKeys.SortArtist,
            sourcePath,
            cancellationToken);

        metadata = metadata with { Album = album.Name, AlbumArtist = artist.Name };
        metadata.Item[MediaKeys.Album] = album.Name;
        metadata.Item[MediaKeys.AlbumArtist] = artist.Name;
        metadata.Item[MediaKeys.SortAlbum] = album.SortName;
        metadata.Item[MediaKeys.SortAlbumArtist] = artist.SortName;
        if (metadata.Item.ContainsKey(MediaKeys.Composer))
        {
            await ResolveItemAsync(
                this.composers,
                metadata.Item,
                MediaKeys.Composer,
                MediaKeys.SortComposer,
                sourcePath,
                cancellationToken);
        }

        return new ResolvedTrackGroups(metadata, album.Id, artist.Id);
    }

    private static async Task ResolveItemAsync(
        MusicGroupResolver resolver,
        Dictionary<string, object> item,
        string nameKey,
        string sortKey,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var group = await resolver.ResolveAsync(
            (string)item[nameKey],
            (string)item[sortKey],
            sourcePath,
            null,
            cancellationToken);

        item[nameKey] = group.Name;
        item[sortKey] = group.SortName;
    }
}
