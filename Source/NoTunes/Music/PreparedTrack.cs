using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record PreparedTrack(SourceTrack Source, string LocalPath, Mp3Metadata Metadata, long Id, long AlbumId, long ArtistId)
{
    public string AssetId => this.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DevicePath => $"{SyncPaths.MusicBucketPrefix}{(ulong)this.Id % SyncProtocol.MusicFolderCount:00}/{(ulong)this.Id:X16}{SyncPaths.Mp3Extension}";
}
