using System.Buffers.Binary;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class PlistTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void HandlesNestedValuesUnicodeAndLongBinaryData(bool binary)
    {
        var data = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        var original = Plist.Dict(("Name", "Björk — 東京 🎵"), ("Count", -123L), ("Allowed", true),
            ("Data", data), ("Nested", new object[] { "hello", Plist.Dict(("Value", 42L)) }));
        var parsed = Plist.Parse(binary ? BinaryPlist.Write(original) : Plist.ToXml(original)).AsDict();
        Assert.That(parsed.Text("Name"), Is.EqualTo("Björk — 東京 🎵"));
        Assert.That(parsed.Number("Count"), Is.EqualTo(-123L));
        Assert.That(parsed.Flag("Allowed"), Is.True);
        Assert.That(parsed["Data"], Is.TypeOf<byte[]>());
        Assert.That((byte[])parsed["Data"], Is.EqualTo(data));
        Assert.That(((object[])parsed["Nested"])[1].AsDict().Number("Value"), Is.EqualTo(42L));
    }

    [Test]
    public void ReadsIndependentBinaryIntegerFixture()
    {
        // Single integer 42 in the Apple binary plist format; not produced by our writer.
        var fixture = new byte[43];
        "bplist00"u8.CopyTo(fixture);
        fixture[8] = 0x10;
        fixture[9] = 42;
        fixture[10] = 8;
        fixture[17] = 1;
        fixture[18] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(fixture.AsSpan(19), 1);
        BinaryPrimitives.WriteUInt64BigEndian(fixture.AsSpan(35), 10);
        Assert.That(BinaryPlist.Read(fixture), Is.EqualTo(42L));
    }

    [Test]
    public void ReadsAndWritesTheIndependent73ByteAtcPingFixture()
    {
        // Minimal binary plist: { Command: Ping, Session: 1 }, as used by Apple's ATC service.
        var fixture = Convert.FromHexString(
            "62706C6973743030D20102030457436F6D6D616E645753657373696F6E5450696E671001" +
            "080D151D220000000000000101000000000000000500000000000000000000000000000024");
        Assert.That(fixture, Has.Length.EqualTo(73));
        var parsed = BinaryPlist.Read(fixture).AsDict();
        Assert.That(parsed.Text("Command"), Is.EqualTo("Ping"));
        Assert.That(parsed.Number("Session"), Is.EqualTo(1L));
        Assert.That(BinaryPlist.Write(Plist.Dict(("Command", "Ping"), ("Session", 1L))), Is.EqualTo(fixture));
    }

    [Test]
    public void RejectsExternalXmlEntities()
    {
        var xml = "<!DOCTYPE plist [<!ENTITY secret SYSTEM 'file:///notunes-secret'>]><plist><string>&secret;</string></plist>";
        Assert.Throws<InvalidDataException>(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [TestCase("<plist><dict><key>x</key></dict></plist>")]
    [TestCase("<plist><dict><key>x</key><true/><key>x</key><false/></dict></plist>")]
    [TestCase("<plist><integer>9223372036854775808</integer></plist>")]
    public void RejectsMalformedXml(string xml) =>
        Assert.Throws<InvalidDataException>(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)));

    [Test]
    public void RejectsBinaryReferenceCycle()
    {
        var bytes = BinaryPlist.Write(new object[] { "hello" });
        bytes[9] = 0; // Root array references itself instead of its string child.
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
    }

    [Test]
    public void RejectsBinaryTruncatedInteger()
    {
        var bytes = BinaryPlist.Write(42L);
        bytes[8] = 0x14; // Claims to contain a 128-bit integer.
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
    }

    [Test]
    public void RejectsInvalidBinaryOffsetTable()
    {
        var bytes = BinaryPlist.Write("hello");
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(bytes.Length - 8), ulong.MaxValue);
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
    }
}
