using System.Buffers.Binary;
using System.IO.Compression;

namespace NoTunes.Apple;

internal static class SavedHostAnchors
{
    private const int RootHeaderBytes = 0xf4;
    private const int HeaderLengthOffset = 4;
    private const int FileLengthOffset = 8;
    private const int MajorVersionOffset = 12;
    private const int MinorVersionOffset = 16;
    private const int SectionCountOffset = 20;
    private const uint CurrentMinorVersion = 117;
    private const uint LegacyMinorVersion = 115;
    private const uint CompressedVersion = 2;
    private const uint UncompressedVersion = 1;
    private const int DatabaseIdentityOffset = 0x48;
    private const int CompressionFlagOffset = 0xa8;
    private const byte CompressionEnabled = 1;
    private const byte CompressionDisabled = 0;
    private const int MaximumSections = 4096;
    private const int DecompressionChunkBytes = 64 * 1024;
    private const int MaximumDecompressedBytes = 256 * 1024 * 1024;
    private const int MinimumSectionHeaderBytes = 16;
    private const uint SectionMagic = 0x6473686d;
    private const int SectionTypeOffset = 12;
    private const uint AnchorSectionType = 11;
    private const int AnchorSectionHeaderBytes = 0x60;
    private const int AnchorSectionBytes = 0x90;
    private const int VoiceMemoAnchorOffset = AnchorSectionHeaderBytes + 2 * sizeof(uint);
    private const uint QualifiedVoiceMemoAnchor = 1;

    private static ReadOnlySpan<byte> RootMagic => "mhbd"u8;

    public static void Validate(byte[] content, long databaseId)
    {
        var count = ReadSectionCount(content, databaseId);
        var sections = ReadSections(content);
        ValidateSections(sections, count);
    }

    private static uint ReadSectionCount(byte[] content, long databaseId)
    {
        if (content.Length < RootHeaderBytes || !content.AsSpan().StartsWith(RootMagic)
            || Number(content, HeaderLengthOffset) != RootHeaderBytes || Number(content, FileLengthOffset) != content.Length
            || Number(content, MinorVersionOffset) is not (CurrentMinorVersion or LegacyMinorVersion)
            || BinaryPrimitives.ReadInt64LittleEndian(content.AsSpan(DatabaseIdentityOffset)) != databaseId)
        {
            throw new InvalidDataException("Unsupported or mismatched saved iTunes catalogue.");
        }

        var count = Number(content, SectionCountOffset);
        if (count is < 1 or > MaximumSections) throw new InvalidDataException("Invalid saved catalogue section count.");

        return count;
    }

    private static byte[] ReadSections(byte[] content)
    {
        var sections = content[RootHeaderBytes..];
        if (Number(content, MajorVersionOffset) == CompressedVersion && content[CompressionFlagOffset] == CompressionEnabled)
        {
            using var input = new MemoryStream(sections, writable: false);
            using var compressed = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[DecompressionChunkBytes];
            int read;
            while ((read = compressed.Read(buffer)) != 0)
            {
                if (output.Length + read > MaximumDecompressedBytes)
                {
                    throw new InvalidDataException("Saved catalogue decompression limit exceeded.");
                }

                output.Write(buffer, 0, read);
            }

            sections = output.ToArray();
        }
        else if (Number(content, MajorVersionOffset) != UncompressedVersion || content[CompressionFlagOffset] != CompressionDisabled)
        {
            throw new InvalidDataException("Unsupported saved catalogue compression.");
        }

        return sections;
    }

    private static void ValidateSections(byte[] sections, uint count)
    {
        var offset = 0;
        var found = false;
        for (var i = 0; i < count; i++)
        {
            if (sections.Length - offset < MinimumSectionHeaderBytes || Number(sections, offset) != SectionMagic)
            {
                throw new InvalidDataException("Invalid saved catalogue section.");
            }

            var header = Number(sections, offset + HeaderLengthOffset);
            var length = Number(sections, offset + FileLengthOffset);
            if (header < MinimumSectionHeaderBytes || length < header || length > sections.Length - offset)
            {
                throw new InvalidDataException("Invalid saved catalogue section size.");
            }

            if (Number(sections, offset + SectionTypeOffset) == AnchorSectionType)
            {
                if (found || header != AnchorSectionHeaderBytes || length != AnchorSectionBytes
                    || Number(sections, offset + VoiceMemoAnchorOffset) != QualifiedVoiceMemoAnchor)
                {
                    throw new InvalidDataException("Unsupported saved VoiceMemo anchor; refusing to overwrite non-Media state.");
                }

                found = true;
            }

            offset += checked((int)length);
        }

        if (!found || offset != sections.Length) throw new InvalidDataException("Missing or malformed saved anchor dataset.");
    }

    private static uint Number(byte[] content, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(offset, sizeof(uint)));
    }
}
