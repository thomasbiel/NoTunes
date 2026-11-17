using System.Buffers.Binary;
using System.Text;

namespace NoTunes.Apple;

internal static class ArtworkDatabase
{
    internal const string Path = "iTunes_Control/Artwork/ArtworkDB";
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int RootHeader = 132;
    private const int SectionHeader = 96;
    private const int ListHeader = 92;
    private const int ImageHeader = 152;
    private const string RootMagic = "mhfd";
    private const string BigEndianRootMagic = "dfhm";
    private const string SectionMagic = "mhsd";
    private const string ImageListMagic = "mhli";
    private const string BigEndianImageListMagic = "ilhm";
    private const string ImageMagic = "mhii";
    private const int HeaderLengthOffset = 4;
    private const int RecordLengthOffset = 8;
    private const int MajorVersionOffset = 12;
    private const int MinorVersionOffset = 16;
    private const uint SupportedMajorVersion = 0;
    private const uint SupportedMinorVersion = 6;
    private const uint LegacyMinorVersion = 2;
    private const int SectionCountOffset = 20;
    private const int CacheCounterOffset = 28;
    private const int SectionKindOffset = 12;
    private const uint ImageSectionKind = 1;
    private const uint LastSectionKind = 3;
    private const uint MaximumSections = 3;
    private const int ImageCountOffset = 8;
    private const uint MaximumImages = 100000;
    private const int ImageIdOffset = 16;
    private static readonly byte[] RootSignature = Encoding.ASCII.GetBytes(RootMagic);
    private static readonly byte[] BigEndianRootSignature = Encoding.ASCII.GetBytes(BigEndianRootMagic);

    public static async Task<uint> HighestAsync(AfcClient afc, uint floor, CancellationToken cancellationToken)
    {
        if (await afc.GetFileInfoAsync(Path, cancellationToken) is null) return floor;

        return Math.Max(floor, Highest(await afc.ReadFileAsync(Path, MaximumBytes, cancellationToken)));
    }

    internal static uint Highest(byte[] bytes)
    {
        var big = bytes.AsSpan().StartsWith(BigEndianRootSignature);
        if (bytes.Length < RootHeader || bytes.Length > MaximumBytes
            || !big && !bytes.AsSpan().StartsWith(RootSignature))
            throw new InvalidDataException("Unsupported saved ArtworkDB.");

        uint Number(int at)
        {
            if (at < 0 || at > bytes.Length - sizeof(uint)) throw new InvalidDataException("Truncated ArtworkDB.");

            return big ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        }

        int Record(int at, string magic, int minimum, int end)
        {
            var expected = Encoding.ASCII.GetBytes(big ? new string(magic.Reverse().ToArray()) : magic);
            if (at < 0 || at > end - minimum || !bytes.AsSpan(at, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException("Invalid ArtworkDB record.");

            var head = Number(at + HeaderLengthOffset);
            var length = Number(at + RecordLengthOffset);
            if (head < minimum || length < head || length > end - at || length % sizeof(uint) != 0)
                throw new InvalidDataException("Invalid ArtworkDB record length.");

            return checked(at + (int)length);
        }

        if (Record(0, RootMagic, RootHeader, bytes.Length) != bytes.Length
            || Number(MajorVersionOffset) != SupportedMajorVersion
            || Number(MinorVersionOffset) is not (SupportedMinorVersion or LegacyMinorVersion))
            throw new InvalidDataException("Unsupported ArtworkDB version or size.");

        var maximum = Number(CacheCounterOffset);
        if (maximum < ArtworkProtocol.CacheIdFloor || maximum > ArtworkProtocol.MaximumCacheId)
            throw new InvalidDataException("Invalid artwork counter.");

        var sections = Number(SectionCountOffset);
        if (sections is < 1 or > MaximumSections) throw new InvalidDataException("Invalid ArtworkDB sections.");

        var offset = checked((int)Number(HeaderLengthOffset));
        var kinds = new HashSet<uint>();
        for (var section = 0; section < sections; section++)
        {
            var end = Record(offset, SectionMagic, SectionHeader, bytes.Length);
            var kind = Number(offset + SectionKindOffset) & ushort.MaxValue;
            if (kind is < ImageSectionKind or > LastSectionKind || !kinds.Add(kind))
                throw new InvalidDataException("Ambiguous artwork sections.");

            if (kind == ImageSectionKind)
            {
                var list = offset + checked((int)Number(offset + HeaderLengthOffset));
                if (list > end - ListHeader || Number(list + HeaderLengthOffset) > end - list)
                    throw new InvalidDataException("Truncated artwork image list.");

                var magic = Encoding.ASCII.GetString(bytes.AsSpan(list, ImageListMagic.Length));
                if (!StringComparer.Ordinal.Equals(magic, big ? BigEndianImageListMagic : ImageListMagic)
                    || Number(list + HeaderLengthOffset) < ListHeader)
                    throw new InvalidDataException("Invalid artwork image list.");

                var count = Number(list + ImageCountOffset);
                if (count > MaximumImages) throw new InvalidDataException("Artwork record limit exceeded.");

                var image = list + checked((int)Number(list + HeaderLengthOffset));
                for (var index = 0; index < count; index++)
                {
                    var next = Record(image, ImageMagic, ImageHeader, end);
                    var id = Number(image + ImageIdOffset);
                    if (id == 0) throw new InvalidDataException("Invalid artwork image ID.");

                    maximum = Math.Max(maximum, id);
                    image = next;
                }

                if (image != end) throw new InvalidDataException("Artwork image list size mismatch.");
            }

            offset = end;
        }

        if (offset != bytes.Length || !kinds.Contains(ImageSectionKind))
            throw new InvalidDataException("Missing artwork image section.");

        return maximum;
    }
}
