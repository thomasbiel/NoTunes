using System.Buffers.Binary;
using System.IO.Compression;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SavedHostAnchorsTests
{
    private const int RootBytes = 244;
    private const int SectionBytes = 144;
    private const int SectionHeaderBytes = 96;
    private const long DatabaseId = 1001;

    [TestCase(115, false)]
    [TestCase(115, true)]
    [TestCase(117, false)]
    [TestCase(117, true)]
    public void Reads_Qualified_Legacy_And_Current_Layouts(int version, bool compressed)
    {
        Assert.DoesNotThrow(() => SavedHostAnchors.Validate(Catalogue(version, compressed), DatabaseId));
    }

    [Test]
    public void Rejects_A_Different_Database_Identity()
    {
        Assert.Throws<InvalidDataException>(() => SavedHostAnchors.Validate(Catalogue(117, false), DatabaseId + 1));
    }

    [Test]
    public void Rejects_Nonqualified_VoiceMemo_State_And_Malformed_Sections()
    {
        var catalogue = Catalogue(117, false);
        BinaryPrimitives.WriteUInt32LittleEndian(catalogue.AsSpan(RootBytes + SectionHeaderBytes + 8), 2);
        Assert.Throws<InvalidDataException>(() => SavedHostAnchors.Validate(catalogue, DatabaseId));
        BinaryPrimitives.WriteUInt32LittleEndian(catalogue.AsSpan(RootBytes + 8), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => SavedHostAnchors.Validate(catalogue, DatabaseId));
    }

    private static byte[] Catalogue(int version, bool compressed)
    {
        var section = new byte[SectionBytes];
        "mhsd"u8.CopyTo(section);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(4), SectionHeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(8), SectionBytes);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(12), 11);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(SectionHeaderBytes + 8), 1);
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var zipper = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
                zipper.Write(section);
            section = output.ToArray();
        }

        var result = new byte[RootBytes + section.Length];
        "mhbd"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), RootBytes);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), compressed ? 2 : 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), version);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(0x48), DatabaseId);
        result[0xa8] = compressed ? (byte)1 : (byte)0;
        section.CopyTo(result, RootBytes);
        return result;
    }
}
