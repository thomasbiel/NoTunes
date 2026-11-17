namespace NoTunes.Music;

internal sealed record CatalogueGroup(
    long RowId,
    long SyncId,
    string Name,
    string SortName,
    long? AlbumArtistRowId,
    string? AlbumArtist)
{
    internal string Key => this.AlbumArtist is null
        ? this.Name : CatalogueReader.GroupKey(this.Name, this.AlbumArtist);

    internal string DisplayName => this.AlbumArtist is null ? this.Name : this.Name + " / " + this.AlbumArtist;

    public bool Matches(CatalogueGroup actual)
    {
        return this.RowId == actual.RowId && this.SyncId == actual.SyncId
            && this.AlbumArtistRowId == actual.AlbumArtistRowId
            && GroupNameComparer.Instance.Equals(this.Name, actual.Name)
            && GroupNameComparer.Instance.Equals(this.AlbumArtist, actual.AlbumArtist)
            && StringComparer.Ordinal.Equals(this.SortName, actual.SortName);
    }
}
