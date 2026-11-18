using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record Mp3Metadata(Dictionary<string, object> Item, Dictionary<string, object> Audio, string Album, string AlbumArtist)
{
    public ArtworkImage? Artwork { get; init; }

    private const int LayerThree = 3;
    private const int MinimumChannels = 1;
    private const int MaximumChannels = 2;
    private const long Mpeg1Layer3 = 301;
    private const long Mpeg2Layer3 = 302;
    private const long Mpeg25Layer3 = 303;
    internal const int MaximumTextLength = 4096;
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";
    internal const string LocationKind = "MPEG audio file";

    public static Mp3Metadata Read(string path, string relativePath, ArtworkCache? artworkCache = null)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var header = file.Properties.Codecs.OfType<TagLib.Mpeg.AudioHeader>().SingleOrDefault();
            if (header.AudioLayer != LayerThree || file.Properties.Duration <= TimeSpan.Zero
                || file.Properties.AudioSampleRate <= 0 || file.Properties.AudioChannels is < MinimumChannels or > MaximumChannels)
            {
                throw new InvalidDataException($"Not a supported MP3 audio stream: {relativePath}");
            }

            var format = header.Version switch
            {
                TagLib.Mpeg.Version.Version1 => Mpeg1Layer3,
                TagLib.Mpeg.Version.Version2 => Mpeg2Layer3,
                TagLib.Mpeg.Version.Version25 => Mpeg25Layer3,
                _ => throw new InvalidDataException($"Unsupported MPEG version: {relativePath}")
            };

            var tag = file.Tag;
            var compilation = file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag { IsCompilation: true };
            var title = Text(tag.Title, Path.GetFileNameWithoutExtension(relativePath));
            var artist = Text(tag.FirstPerformer, UnknownArtist);
            var artistSort = Text(tag.FirstPerformerSort, artist);
            var album = Text(tag.Album, UnknownAlbum);
            var explicitAlbumArtist = !string.IsNullOrWhiteSpace(tag.FirstAlbumArtist);
            if (compilation && !explicitAlbumArtist)
            {
                throw new InvalidDataException($"Compilation requires an explicit album artist before sync: {relativePath}");
            }

            var albumArtist = Text(tag.FirstAlbumArtist, artist);
            var albumArtistSort = Text(tag.FirstAlbumArtistSort, explicitAlbumArtist ? albumArtist : artistSort);
            var item = Plist.Dict(
                (MediaKeys.Title, title),
                (MediaKeys.Artist, artist),
                (MediaKeys.Album, album),
                (MediaKeys.AlbumArtist, albumArtist),
                (MediaKeys.SortTitle, Text(tag.TitleSort, title)),
                (MediaKeys.SortArtist, artistSort),
                (MediaKeys.SortAlbum, Text(tag.AlbumSort, album)),
                (MediaKeys.SortAlbumArtist, albumArtistSort),
                (MediaKeys.Genre, Text(tag.FirstGenre, string.Empty)),
                (MediaKeys.IsSong, true),
                (MediaKeys.RememberBookmark, false),
                (MediaKeys.TotalMilliseconds, (long)file.Properties.Duration.TotalMilliseconds),
                (MediaKeys.ArtworkChecksum, 0L));

            if (compilation)
            {
                item[MediaKeys.IsCompilation] = true;
            }

            AddNumber(item, MediaKeys.TrackNumber, tag.Track);
            AddNumber(item, MediaKeys.TrackCount, tag.TrackCount);
            AddNumber(item, MediaKeys.DiscNumber, tag.Disc);
            AddNumber(item, MediaKeys.DiscCount, tag.DiscCount);
            AddNumber(item, MediaKeys.Year, tag.Year);
            if (!string.IsNullOrWhiteSpace(tag.FirstComposer))
            {
                item[MediaKeys.Composer] = Text(tag.FirstComposer, string.Empty);
                item[MediaKeys.SortComposer] = Text(tag.FirstComposerSort, tag.FirstComposer);
            }

            if (!string.IsNullOrWhiteSpace(tag.Comment))
                item[MediaKeys.Comment] = Text(tag.Comment, string.Empty);
            var audio = Plist.Dict(
                (MediaKeys.AudioFormat, format),
                (MediaKeys.SampleRate, (double)file.Properties.AudioSampleRate),
                (MediaKeys.Channels, (long)file.Properties.AudioChannels),
                (MediaKeys.BitRate, (long)file.Properties.AudioBitrate),
                (MediaKeys.Duration, (long)Math.Round(file.Properties.Duration.TotalSeconds * file.Properties.AudioSampleRate)));

            return new Mp3Metadata(
                item,
                audio,
                album,
                albumArtist)
            {
                Artwork = ReadArtwork(tag, artworkCache)
            };
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            throw new InvalidDataException($"Cannot read MP3 metadata: {relativePath}", e);
        }
    }

    public static bool HasArtwork(FileStream source)
    {
        using var file = TagLib.File.Create(new SourceTagFile(source), TagLib.ReadStyle.None);
        return file.Tag.Pictures.Length != 0;
    }

    private static ArtworkImage? ReadArtwork(TagLib.Tag tag, ArtworkCache? artworkCache)
    {
        var picture = tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
            ?? tag.Pictures.FirstOrDefault();

        if (picture is null) return null;

        var bytes = picture.Data.Data;
        return artworkCache is null ? ArtworkImage.Read(bytes) : artworkCache.Read(bytes);
    }

    private static string Text(string? value, string fallback)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (result.Length > MaximumTextLength || result.Contains('\0')) throw new InvalidDataException("Invalid MP3 metadata text.");

        return result;
    }

    private static void AddNumber(Dictionary<string, object> item, string key, uint value)
    {
        if (value != 0)
            item[key] = (long)value;
    }
}
