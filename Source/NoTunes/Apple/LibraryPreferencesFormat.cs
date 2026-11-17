namespace NoTunes.Apple;

internal static class LibraryPreferencesFormat
{
    internal const int Length = 1232;
    internal const int MajorVersionOffset = 4;
    internal const int MinorVersionOffset = 6;
    internal const int ConfiguredOffset = 8;
    internal const ushort MajorVersion = 1;
    internal const ushort MinorVersion = 25;
    internal const byte Configured = 1;
    internal const int CatalogueIdentityOffset = 12;
    internal const int UserIdentityOffset = CatalogueIdentityOffset + sizeof(ulong);
    internal const string PreferencesKey = "iPodPrefs";
    internal const string DevicesVersionKey = "DevicesVersion";
    internal const string DevicesVersion = "1.6.4";
    internal const string AudiobookPlaylistIdsKey = "AudiobookPlaylistIDs";
    internal const string AudiobookTrackIdsKey = "AudiobookTrackIDs";
    internal const string BookTrackIdsKey = "BookTrackIDs";
    internal const string LibraryBookTrackIdsKey = "LibraryBookTrackIDs";
    internal const string MoviePlaylistIdsKey = "MoviePlaylistIDs";
    internal const string MovieTrackIdsKey = "MovieTrackIDs";
    internal const string MusicAlbumIdsKey = "MusicAlbumIDs";
    internal const string MusicArtistIdsKey = "MusicArtistIDs";
    internal const string MusicGenreIdsKey = "MusicGenreIDs";
    internal const string MusicPlaylistIdsKey = "MusicPlaylistIDs";
    internal const string MusicTrackIdsKey = "MusicTrackIDs";
    internal const string PodcastChannelIdsKey = "PodcastChannelIDs";
    internal const string PodcastPlaylistIdsKey = "PodcastPlaylistIDs";
    internal const string PodcastTrackIdsKey = "PodcastTrackIDs";
    internal const string TvShowAlbumIdsKey = "TVShowAlbumIDs";
    internal const string TvShowPlaylistIdsKey = "TVShowPlaylistIDs";
    internal const string TvShowSeriesIdsKey = "TVShowSeriesIDs";
    internal const string TvShowTrackIdsKey = "TVShowTrackIDs";

    internal static ReadOnlySpan<byte> Magic => "frpd"u8;
}
