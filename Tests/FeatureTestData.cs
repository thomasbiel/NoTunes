using NoTunes.Apple;
using NoTunes.Music;

namespace NoTunes.Tests;

internal static class FeatureTestData
{
    public static byte[] Png()
    {
        using var output = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(
            new byte[] { 255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255 },
            2,
            2,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
            output);
        return output.ToArray();
    }

    public static PreparedTrack Covered(bool artworkOnly = false)
    {
        var track = SyncTestData.Track();
        return track with
        {
            IsArtworkOnly = artworkOnly,
            Metadata = track.Metadata with { Artwork = ArtworkImage.Read(Png()) with { CacheId = 101 } }
        };
    }

    public static DeviceTrack Stored(PreparedTrack track)
    {
        var item = track.Metadata.Item;
        var audio = track.Metadata.Audio;
        return new DeviceTrack(
            track.Id,
            (string)item[MediaKeys.Title],
            track.DevicePath,
            track.Source.Size)
        {
            Metadata = new TrackMetadata
            {
                AlbumId = track.AlbumId,
                AlbumArtistId = track.ArtistId,
                Album = track.Metadata.Album,
                AlbumArtist = track.Metadata.AlbumArtist,
                Artist = (string)item[MediaKeys.Artist],
                Composer = item.GetValueOrDefault(MediaKeys.Composer) as string,
                Genre = (string)item[MediaKeys.Genre],
                SortTitle = (string)item[MediaKeys.SortTitle],
                SortAlbum = (string)item[MediaKeys.SortAlbum],
                SortAlbumArtist = (string)item[MediaKeys.SortAlbumArtist],
                SortArtist = (string)item[MediaKeys.SortArtist],
                SortComposer = item.GetValueOrDefault(MediaKeys.SortComposer) as string,
                Comment = item.GetValueOrDefault(MediaKeys.Comment) as string,
                TrackNumber = item.GetValueOrDefault(MediaKeys.TrackNumber) as long?,
                TrackCount = item.GetValueOrDefault(MediaKeys.TrackCount) as long?,
                DiscNumber = item.GetValueOrDefault(MediaKeys.DiscNumber) as long? ?? 1,
                DiscCount = item.GetValueOrDefault(MediaKeys.DiscCount) as long?,
                Year = item.GetValueOrDefault(MediaKeys.Year) as long?,
                Compilation = item.GetValueOrDefault(MediaKeys.IsCompilation) is true ? 1 : 0,
                TotalMilliseconds = Math.Floor((long)audio[MediaKeys.Duration] * 1000.0 / (double)audio[MediaKeys.SampleRate]),
                MediaKind = 1,
                AudioFormat = (long)audio[MediaKeys.AudioFormat],
                BitRate = (long)audio[MediaKeys.BitRate],
                SampleRate = (double)audio[MediaKeys.SampleRate],
                Duration = (long)audio[MediaKeys.Duration],
                RememberBookmark = 0
            }
        };
    }
}
