namespace NoTunes.Music;

internal sealed record TrackMetadata
{
    private const long SongMediaKind = 1;
    private const long DefaultDiscNumber = 1;
    private const long StoredTrue = 1;
    private const long MaximumMillisecondRoundingDifference = 1;

    public long? AlbumRowId { get; init; }

    public long? AlbumArtistRowId { get; init; }

    public long? ItemArtistRowId { get; init; }

    public long? ComposerRowId { get; init; }

    public long? GenreId { get; init; }

    public long? AlbumId { get; init; }

    public long? AlbumArtistId { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Artist { get; init; }

    public string? Composer { get; init; }

    public string? Genre { get; init; }

    public string? SortTitle { get; init; }

    public string? Comment { get; init; }

    public string? SortAlbum { get; init; }

    public string? SortAlbumArtist { get; init; }

    public string? SortArtist { get; init; }

    public string? SortComposer { get; init; }

    public long? TrackNumber { get; init; }

    public long? TrackCount { get; init; }

    public long? DiscNumber { get; init; }

    public long? DiscCount { get; init; }

    public long? Year { get; init; }

    public long? Compilation { get; init; }

    public double? TotalMilliseconds { get; init; }

    public long? MediaKind { get; init; }

    public long? AudioFormat { get; init; }

    public long? BitRate { get; init; }

    public double? SampleRate { get; init; }

    public long? Duration { get; init; }

    public long? RememberBookmark { get; init; }

    public bool Matches(PreparedTrack track)
    {
        var item = track.Metadata.Item;
        var audio = track.Metadata.Audio;
        // The receiver can retain the supplied milliseconds or derive them from rounded audio samples.
        var milliseconds = Math.Floor((long)audio[MediaKeys.Duration]
            * (double)TimeSpan.MillisecondsPerSecond / (double)audio[MediaKeys.SampleRate]);
        var suppliedMilliseconds = Number(item, MediaKeys.TotalMilliseconds);
        var sameMilliseconds = this.TotalMilliseconds == milliseconds
            || this.TotalMilliseconds == suppliedMilliseconds
                && Math.Abs(suppliedMilliseconds - milliseconds) <= MaximumMillisecondRoundingDifference;

        return this.AlbumId == track.AlbumId && this.AlbumArtistId == track.ArtistId
            && GroupNameComparer.Instance.Equals(this.Album, track.Metadata.Album)
            && GroupNameComparer.Instance.Equals(this.AlbumArtist, track.Metadata.AlbumArtist)
            && GroupNameComparer.Instance.Equals(this.Artist, (string)item[MediaKeys.Artist])
            && GroupNameComparer.Instance.Equals(this.Composer ?? string.Empty, Text(item, MediaKeys.Composer))
            && GroupNameComparer.Instance.Equals(this.Genre ?? string.Empty, Text(item, MediaKeys.Genre))
            && StringComparer.Ordinal.Equals(this.SortTitle ?? (string)item[MediaKeys.Title], Text(item, MediaKeys.SortTitle))
            && StringComparer.Ordinal.Equals(this.SortAlbum, Text(item, MediaKeys.SortAlbum))
            && StringComparer.Ordinal.Equals(this.SortAlbumArtist, Text(item, MediaKeys.SortAlbumArtist))
            && StringComparer.Ordinal.Equals(this.SortArtist, Text(item, MediaKeys.SortArtist))
            && StringComparer.Ordinal.Equals(this.SortComposer ?? string.Empty, Text(item, MediaKeys.SortComposer))
            && StringComparer.Ordinal.Equals(this.Comment ?? string.Empty, Text(item, MediaKeys.Comment))
            && (this.TrackNumber ?? 0) == Number(item, MediaKeys.TrackNumber)
            && (this.TrackCount ?? 0) == Number(item, MediaKeys.TrackCount)
            && this.DiscNumber == Number(item, MediaKeys.DiscNumber, DefaultDiscNumber)
            && (this.DiscCount ?? 0) == Number(item, MediaKeys.DiscCount)
            && (this.Year ?? 0) == Number(item, MediaKeys.Year)
            && (this.Compilation ?? 0) == (item.GetValueOrDefault(MediaKeys.IsCompilation) is true ? StoredTrue : 0)
            && sameMilliseconds && this.MediaKind == SongMediaKind
            && this.AudioFormat == (long)audio[MediaKeys.AudioFormat]
            && this.BitRate == (long)audio[MediaKeys.BitRate]
            && this.SampleRate == (double)audio[MediaKeys.SampleRate]
            && this.Duration == (long)audio[MediaKeys.Duration]
            && this.RememberBookmark == 0;
    }

    private static string Text(IReadOnlyDictionary<string, object> item, string key)
    {
        return item.TryGetValue(key, out var value) ? (string)value : string.Empty;
    }

    private static long Number(IReadOnlyDictionary<string, object> item, string key, long missing = 0)
    {
        return item.TryGetValue(key, out var value) ? (long)value : missing;
    }
}
