using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record PreparedTrack(
    SourceTrack Source,
    string LocalPath,
    Mp3Metadata Metadata,
    long Id,
    long AlbumId,
    long ArtistId)
{
    public SyncWorkspace? Workspace { get; init; }

    public bool IsArtworkOnly { get; init; }

    public string? ExistingDevicePath { get; init; }

    public string AssetId => this.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DevicePath => this.ExistingDevicePath
        ?? $"{SyncPaths.MusicBucketPrefix}{(ulong)this.Id % SyncProtocol.MusicFolderCount:00}/"
        + $"{(ulong)this.Id:X16}{SyncPaths.Mp3Extension}";
}
