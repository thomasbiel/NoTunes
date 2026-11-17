using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using static NoTunes.Apple.LibraryPreferencesFormat;

namespace NoTunes.Apple;

internal static class LibraryPreferences
{
    private const int PodcastCatalogOffset = 0x60;
    private const int NameLength = 64;
    private static readonly IReadOnlyList<int> NameOffsets = [0x180, 0x1c0, 0x240, 0x280];
    private static readonly IReadOnlyList<string> EmptyIdentifierListKeys =
    [
        AudiobookPlaylistIdsKey,
        AudiobookTrackIdsKey,
        BookTrackIdsKey,
        LibraryBookTrackIdsKey,
        MoviePlaylistIdsKey,
        MovieTrackIdsKey,
        MusicAlbumIdsKey,
        MusicArtistIdsKey,
        MusicGenreIdsKey,
        MusicPlaylistIdsKey,
        MusicTrackIdsKey,
        PodcastChannelIdsKey,
        PodcastPlaylistIdsKey,
        PodcastTrackIdsKey,
        TvShowAlbumIdsKey,
        TvShowPlaylistIdsKey,
        TvShowSeriesIdsKey,
        TvShowTrackIdsKey
    ];

    // Opaque adoption-profile bytes retain their original offsets and values.
    private static readonly IReadOnlyList<(int Offset, byte Value)> AdoptionFlags =
    [
        (0x0b, 1), (0x23, 1), (0x30, 1), (0x35, 2),
        (0x4b, 1), (0x4c, 1), (0x59, 1), (0x5b, 4), (0x68, 2), (0x7a, 1), (0x99, 2),
        (0x9d, 1), (0x9e, 1), (0xa9, 1), (0xab, 1), (0xae, 1), (0xf4, 255),
        (0x10f, 1), (0x117, 15), (0x11d, 1), (0x11f, 1), (0x124, 1), (0x125, 1), (0x126, 128)
    ];

    public static byte[] Create()
    {
        var raw = new byte[Length];
        Magic.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(MajorVersionOffset), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(MinorVersionOffset), MinorVersion);
        raw[ConfiguredOffset] = Configured;

        foreach (var (offset, value) in AdoptionFlags)
        {
            raw[offset] = value;
        }

        var catalog = NewId();
        var user = NewId();
        while (user == catalog)
        {
            user = NewId();
        }

        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(CatalogueIdentityOffset), catalog);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(UserIdentityOffset), user);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(PodcastCatalogOffset), catalog);
        foreach (var offset in NameOffsets)
        {
            Encoding.ASCII.GetBytes(AppleClient.Name).AsSpan(0, Math.Min(AppleClient.Name.Length, NameLength - 1))
                .CopyTo(raw.AsSpan(offset));
        }

        return raw;
    }

    public static byte[] Wrap(byte[] raw)
    {
        _ = LibraryIdentity.Parse(raw);
        var values = Plist.Dict((DevicesVersionKey, DevicesVersion), (PreferencesKey, raw));
        foreach (var key in EmptyIdentifierListKeys)
        {
            values.Add(key, Array.Empty<object>());
        }

        return Plist.ToXml(values);
    }

    public static long NewId()
    {
        while (true)
        {
            var id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long)));
            if ((ulong)id >= SyncProtocol.MinimumPersistentId) return id;
        }
    }
}
