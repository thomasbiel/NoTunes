using System.Buffers.Binary;

namespace NoTunes.Apple;

internal sealed class ArtworkDatabaseReader(byte[] bytes)
{
    private const int RootHeader = 132;
    private const int SectionHeader = 96;
    private const int ListHeader = 92;
    private const int ImageHeader = 152;
    private const string RootMagic = "mhfd";
    private const string SectionMagic = "mhsd";
    private const string ImageListMagic = "mhli";
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
    private readonly bool bigEndian = bytes.AsSpan().StartsWith(BigEndianRootSignature);

    private static ReadOnlySpan<byte> BigEndianRootSignature => "dfhm"u8;

    public uint ReadHighest()
    {
        if (bytes.Length < RootHeader || bytes.Length > ArtworkDatabase.MaximumBytes || !this.HasMagic(0, RootMagic))
        {
            throw new InvalidDataException("Unsupported saved ArtworkDB.");
        }

        this.ValidateHeader();
        var maximum = this.Number(CacheCounterOffset);
        if (maximum is < ArtworkProtocol.CacheIdFloor or > ArtworkProtocol.MaximumCacheId)
        {
            throw new InvalidDataException("Invalid artwork counter.");
        }

        var sections = this.Number(SectionCountOffset);
        if (sections is < 1 or > MaximumSections) throw new InvalidDataException("Invalid ArtworkDB sections.");

        return this.ReadSections(sections, maximum);
    }

    private void ValidateHeader()
    {
        if (this.RecordEnd(
            0,
            RootMagic,
            RootHeader,
            bytes.Length) != bytes.Length
            || this.Number(MajorVersionOffset) != SupportedMajorVersion
            || this.Number(MinorVersionOffset) is not (SupportedMinorVersion or LegacyMinorVersion))
        {
            throw new InvalidDataException("Unsupported ArtworkDB version or size.");
        }
    }

    private uint ReadSections(uint sections, uint maximum)
    {
        var offset = checked((int)this.Number(HeaderLengthOffset));
        var kinds = new HashSet<uint>();
        for (var section = 0; section < sections; section++)
        {
            var end = this.RecordEnd(
                offset,
                SectionMagic,
                SectionHeader,
                bytes.Length);

            var kind = this.Number(offset + SectionKindOffset) & ushort.MaxValue;
            if (kind is < ImageSectionKind or > LastSectionKind || !kinds.Add(kind))
            {
                throw new InvalidDataException("Ambiguous artwork sections.");
            }

            if (kind == ImageSectionKind)
            {
                maximum = this.ReadImages(offset, end, maximum);
            }

            offset = end;
        }

        if (offset != bytes.Length || !kinds.Contains(ImageSectionKind))
        {
            throw new InvalidDataException("Missing artwork image section.");
        }

        return maximum;
    }

    private uint ReadImages(int section, int end, uint maximum)
    {
        var list = section + checked((int)this.Number(section + HeaderLengthOffset));
        if (list > end - ListHeader || this.Number(list + HeaderLengthOffset) > end - list)
        {
            throw new InvalidDataException("Truncated artwork image list.");
        }

        if (!this.HasMagic(list, ImageListMagic) || this.Number(list + HeaderLengthOffset) < ListHeader)
        {
            throw new InvalidDataException("Invalid artwork image list.");
        }

        var count = this.Number(list + ImageCountOffset);
        if (count > MaximumImages) throw new InvalidDataException("Artwork record limit exceeded.");

        var image = list + checked((int)this.Number(list + HeaderLengthOffset));
        for (var index = 0; index < count; index++)
        {
            var next = this.RecordEnd(
                image,
                ImageMagic,
                ImageHeader,
                end);

            var id = this.Number(image + ImageIdOffset);
            if (id == 0) throw new InvalidDataException("Invalid artwork image ID.");

            maximum = Math.Max(maximum, id);
            image = next;
        }

        if (image != end) throw new InvalidDataException("Artwork image list size mismatch.");

        return maximum;
    }

    private int RecordEnd(int offset, string magic, int minimum, int end)
    {
        if (offset < 0 || offset > end - minimum || !this.HasMagic(offset, magic))
        {
            throw new InvalidDataException("Invalid ArtworkDB record.");
        }

        var header = this.Number(offset + HeaderLengthOffset);
        var length = this.Number(offset + RecordLengthOffset);
        if (header < minimum || length < header || length > end - offset || length % sizeof(uint) != 0)
        {
            throw new InvalidDataException("Invalid ArtworkDB record length.");
        }

        return checked(offset + (int)length);
    }

    private bool HasMagic(int offset, string magic)
    {
        var actual = bytes.AsSpan(offset, magic.Length);
        for (var index = 0; index < magic.Length; index++)
        {
            var expected = magic[this.bigEndian ? magic.Length - index - 1 : index];
            if (actual[index] != expected) return false;
        }

        return true;
    }

    private uint Number(int offset)
    {
        if (offset < 0 || offset > bytes.Length - sizeof(uint)) throw new InvalidDataException("Truncated ArtworkDB.");

        return this.bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    }
}
