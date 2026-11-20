using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record JournalTrack(
    SourceTrack Source,
    long Id,
    long AlbumId,
    long ArtistId,
    byte[] Metadata,
    JournalArtwork? Artwork,
    bool IsArtworkOnly,
    string? ExistingDevicePath)
{
    private const string ItemKey = "Item";
    private const string AudioKey = "Audio";
    private const string AlbumKey = "Album";
    private const string AlbumArtistKey = "AlbumArtist";

    public static JournalTrack Capture(PreparedTrack track)
    {
        return new JournalTrack(
            track.Source,
            track.Id,
            track.AlbumId,
            track.ArtistId,
            BinaryPlist.Write(Plist.Dict(
                (ItemKey, track.Metadata.Item),
                (AudioKey, track.Metadata.Audio),
                (AlbumKey, track.Metadata.Album),
                (AlbumArtistKey, track.Metadata.AlbumArtist))),
            track.Metadata.Artwork is { } art ? new JournalArtwork(art.CacheId, art.Length, art.Hash) : null,
            track.IsArtworkOnly,
            track.ExistingDevicePath);
    }

    public PreparedTrack Restore(string directory)
    {
        var values = Plist.Parse(this.Metadata).AsDict();
        return new PreparedTrack(
            this.Source,
            AssetPath(directory, this.Id, SyncPaths.Mp3Extension),
            new Mp3Metadata(
                values.Dictionary(ItemKey),
                values.Dictionary(AudioKey),
                values.Text(AlbumKey),
                values.Text(AlbumArtistKey))
            {
                Artwork = this.Artwork?.Restore(AssetPath(directory, this.Id, SyncPaths.JpegExtension))
            },
            this.Id,
            this.AlbumId,
            this.ArtistId)
        {
            IsArtworkOnly = this.IsArtworkOnly,
            ExistingDevicePath = this.ExistingDevicePath
        };
    }

    public static string AssetPath(string directory, long id, string extension)
    {
        return Path.Combine(directory, ((ulong)id).ToString("X16") + extension);
    }
}
