using System.Buffers.Binary;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ArtworkDatabaseTests
{
    private const int RootBytes = 132;
    private const int SectionBytes = 96;
    private const int ListBytes = 92;
    private const int ImageBytes = 152;

    [TestCase(false)]
    [TestCase(true)]
    public void Reserves_Above_Both_The_Saved_Counter_And_Image_Ids(bool bigEndian)
    {
        var bytes = Fixture(bigEndian, 101, 700);
        Assert.That(ArtworkDatabase.Highest(bytes), Is.EqualTo(700));
        Assert.That(ArtworkDatabase.Highest(Fixture(bigEndian, 900, 700)), Is.EqualTo(900));
    }

    [Test]
    public void Rejects_Truncated_Or_Ambiguous_Saved_Artwork()
    {
        var bytes = Fixture(false, 101, 700);
        Assert.Throws<InvalidDataException>(() => ArtworkDatabase.Highest(bytes[..^1]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RootBytes + SectionBytes + 8), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => ArtworkDatabase.Highest(bytes));
    }

    private static byte[] Fixture(bool bigEndian, uint next, uint imageId)
    {
        var bytes = new byte[RootBytes + SectionBytes + ListBytes + ImageBytes];
        void Number(int offset, uint value)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        }

        void Header(int offset, string magic, uint head, uint total)
        {
            Encoding.ASCII.GetBytes(bigEndian ? new string(magic.Reverse().ToArray()) : magic).CopyTo(bytes, offset);
            Number(offset + 4, head);
            Number(offset + 8, total);
        }

        Header(0, "mhfd", RootBytes, (uint)bytes.Length);
        Number(16, 6);
        Number(20, 1);
        Number(28, next);
        Header(RootBytes, "mhsd", SectionBytes, (uint)(bytes.Length - RootBytes));
        Number(RootBytes + 12, 1);
        var list = RootBytes + SectionBytes;
        Header(list, "mhli", ListBytes, 1);
        var image = list + ListBytes;
        Header(image, "mhii", ImageBytes, ImageBytes);
        Number(image + 16, imageId);
        return bytes;
    }
}
