using System.Buffers.Binary;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class PlistTests
{
    [Test]
    public void Handles_Nested_Values_Unicode_And_Long_Binary_Data([Values] bool binary)
    {
        var data = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        var original = Plist.Dict(
            ("Name", "Björk — 東京 🎵"),
            ("Count", -123L),
            ("Allowed", true),
            ("Data", data),
            ("Nested", new object[] { "hello", Plist.Dict(("Value", 42L)) }));
        var parsed = Plist.Parse(binary ? BinaryPlist.Write(original) : Plist.ToXml(original)).AsDict();
        Assert.That(parsed.Text("Name"), Is.EqualTo("Björk — 東京 🎵"));
        Assert.That(parsed.Number("Count"), Is.EqualTo(-123L));
        Assert.That(parsed.Flag("Allowed"), Is.True);
        Assert.That(parsed["Data"], Is.TypeOf<byte[]>());
        Assert.That((byte[])parsed["Data"], Is.EqualTo(data));
        Assert.That(((object[])parsed["Nested"])[1].AsDict().Number("Value"), Is.EqualTo(42L));
    }

    [Test]
    public void Reads_Independent_Binary_Integer_Fixture()
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
    public void Reads_And_Writes_The_Independent_73_Byte_Atc_Ping_Fixture()
    {
        // Minimal binary plist: { Command: Ping, Session: 1 }, as used by Apple's ATC service.
        var fixture = Convert.FromHexString("62706C6973743030D20102030457436F6D6D616E645753657373696F6E5450696E671001" + "080D151D220000000000000101000000000000000500000000000000000000000000000024");
        Assert.That(fixture, Has.Length.EqualTo(73));
        var parsed = BinaryPlist.Read(fixture).AsDict();
        Assert.That(parsed.Text("Command"), Is.EqualTo("Ping"));
        Assert.That(parsed.Number("Session"), Is.EqualTo(1L));
        Assert.That(BinaryPlist.Write(Plist.Dict(("Command", "Ping"), ("Session", 1L))), Is.EqualTo(fixture));
    }

    [Test]
    public void Rejects_External_Xml_Entities()
    {
        var xml = "<!DOCTYPE plist [<!ENTITY secret SYSTEM 'file:///notunes-secret'>]><plist><string>&secret;</string></plist>";
        Assert.Throws<InvalidDataException>(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [TestCase("<plist><dict><key>x</key></dict></plist>")]
    [TestCase("<plist><dict><key>x</key><true/><key>x</key><false/></dict></plist>")]
    [TestCase("<plist><integer>9223372036854775808</integer></plist>")]
    public void Rejects_Malformed_Xml(string xml)
    {
        Assert.Throws<InvalidDataException>(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [TestCase("<true>false</true>")]
    [TestCase("<true><false/></true>")]
    [TestCase("<false>true</false>")]
    [TestCase("<string><true/></string>")]
    [TestCase("<integer><string>1</string></integer>")]
    [TestCase("<dict><key><string>x</string></key><true/></dict>")]
    public void Rejects_Malformed_Xml_Scalars(string value)
    {
        Assert.That(() => Plist.Parse(Encoding.UTF8.GetBytes($"<plist>{value}</plist>")), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("<array>", "<true/>", "</array>")]
    [TestCase("<dict>", "<key>k{0}</key><true/>", "</dict>")]
    public void Rejects_Xml_That_Exceeds_The_Object_Limit(string start, string entry, string end)
    {
        var count = start == "<array>" ? Plist.MaxObjectCount : Plist.MaxObjectCount / 2;
        var xml = new StringBuilder("<plist>" + start);
        for (var i = 0; i < count; i++)
        {
            xml.AppendFormat(entry, i);
        }

        xml.Append(end).Append("</plist>");
        Assert.That(() => Plist.Parse(Encoding.UTF8.GetBytes(xml.ToString())), Throws.TypeOf<InvalidDataException>().With.Message.Contains("complex"));
    }

    [Test]
    public void Accepts_Xml_At_The_Object_Limit()
    {
        var xml = "<plist><array>" + string.Concat(Enumerable.Repeat("<true/>", Plist.MaxObjectCount - 1)) + "</array></plist>";
        Assert.That((object[])Plist.Parse(Encoding.UTF8.GetBytes(xml)), Has.Length.EqualTo(Plist.MaxObjectCount - 1));
    }

    [TestCase(Plist.MaxNestingDepth, true)]
    [TestCase(Plist.MaxNestingDepth + 1, false)]
    public void Enforces_The_Xml_Nesting_Limit(int depth, bool accepted)
    {
        var xml = "<plist>" + string.Concat(Enumerable.Repeat("<array>", depth)) + "<true/>" + string.Concat(Enumerable.Repeat("</array>", depth)) + "</plist>";
        if (accepted)
            Assert.DoesNotThrow(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)));
        else
            Assert.That(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("<plist/>")]
    [TestCase("<plist><true/><false/></plist>")]
    [TestCase("<plist><true/></plist><plist><false/></plist>")]
    public void Rejects_Xml_Without_Exactly_One_Root_Value(string xml)
    {
        Assert.That(() => Plist.Parse(Encoding.UTF8.GetBytes(xml)), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Reads_Empty_And_Whitespace_Xml_Values_With_Comments()
    {
        var xml = "<plist><!-- before --><dict><key>empty</key><string/><key>space</key><string> </string>" + "<key>array</key><array/><key>dict</key><dict/><key>flag</key><true></true></dict><!-- after --></plist>";
        var result = Plist.Parse(Encoding.UTF8.GetBytes(xml)).AsDict();
        Assert.That(result.Text("empty"), Is.Empty);
        Assert.That(result.Text("space"), Is.EqualTo(" "));
        Assert.That((object[])result["array"], Is.Empty);
        Assert.That(result["dict"].AsDict(), Is.Empty);
        Assert.That(result.Flag("flag"), Is.True);
    }

    [Test]
    public void Rejects_Binary_Reference_Cycle()
    {
        var bytes = BinaryPlist.Write(new object[] { "hello" });
        bytes[9] = 0; // Root array references itself instead of its string child.
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
    }

    [Test]
    public void Rejects_Binary_Truncated_Integer()
    {
        var bytes = BinaryPlist.Write(42L);
        bytes[8] = 0x14; // Claims to contain a 128-bit integer.
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
    }

    [Test]
    public void Rejects_Invalid_Binary_Offset_Table()
    {
        var bytes = BinaryPlist.Write("hello");
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(bytes.Length - 8), ulong.MaxValue);
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
    }

    [Test]
    public void Reuses_Shared_Binary_Data_Instead_Of_Copying_It_For_Every_Reference()
    {
        var parsed = (object[])BinaryPlist.Read(RepeatedBinaryData(64, distinctObjects: false));
        Assert.That(parsed, Has.Length.EqualTo(64));
        Assert.That((byte[])parsed[0], Has.Length.EqualTo(1024 * 1024));
        Assert.That(parsed.All(item => ReferenceEquals(item, parsed[0])), Is.True);
    }

    [Test]
    public void Rejects_Decoded_Data_That_Exceeds_The_Memory_Budget()
    {
        var fixture = RepeatedBinaryData(80, distinctObjects: true);
        Assert.That(fixture.Length, Is.LessThan(Plist.MaxBytes));
        Assert.That(() => BinaryPlist.Read(fixture), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Decoded"));
    }

    private static byte[] RepeatedBinaryData(int references, bool distinctObjects)
    {
        // Independent fixture: references share one blob, optionally through different object IDs.
        const int length = 1024 * 1024;
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        stream.WriteByte(0xaf);
        stream.WriteByte(0x10);
        stream.WriteByte((byte)references);
        for (var i = 0; i < references; i++)
        {
            stream.WriteByte(distinctObjects ? (byte)(i + 1) : (byte)1);
        }

        var blobOffset = (uint)stream.Position;
        stream.WriteByte(0x4f);
        stream.WriteByte(0x12);
        var number = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(number, length);
        stream.Write(number, 0, 4);
        stream.Write(new byte[length]);
        var table = (ulong)stream.Position;
        BinaryPrimitives.WriteUInt32BigEndian(number, 8);
        stream.Write(number, 0, 4);
        var blobCount = distinctObjects ? references : 1;
        BinaryPrimitives.WriteUInt32BigEndian(number, blobOffset);
        for (var i = 0; i < blobCount; i++)
        {
            stream.Write(number, 0, 4);
        }

        stream.Write(new byte[6]);
        stream.WriteByte(4);
        stream.WriteByte(1);
        BinaryPrimitives.WriteUInt64BigEndian(number, (ulong)blobCount + 1);
        stream.Write(number);
        BinaryPrimitives.WriteUInt64BigEndian(number, 0);
        stream.Write(number);
        BinaryPrimitives.WriteUInt64BigEndian(number, table);
        stream.Write(number);
        return stream.ToArray();
    }

    [Test]
    public void Shared_Arrays_Cannot_Bypass_The_Nesting_Limit()
    {
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        stream.Write([0xa2, 1, 2, 0xa1, 65]);
        var offsets = new List<byte>
        {
            8,
            11
        };
        for (var index = 2; index <= 64; index++)
        {
            offsets.Add((byte)stream.Position);
            stream.WriteByte(0xa1);
            stream.WriteByte(index == 64 ? (byte)1 : (byte)(index + 1));
        }

        offsets.Add((byte)stream.Position);
        stream.WriteByte(0x54);
        stream.Write("leaf"u8);
        var table = (ulong)stream.Position;
        stream.Write([.. offsets]);
        stream.Write(new byte[32]);
        var fixture = stream.ToArray();
        var trailer = fixture.Length - 32;
        fixture[trailer + 6] = 1;
        fixture[trailer + 7] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(fixture.AsSpan(trailer + 8), (ulong)offsets.Count);
        BinaryPrimitives.WriteUInt64BigEndian(fixture.AsSpan(trailer + 24), table);
        Assert.That(() => BinaryPlist.Read(fixture), Throws.TypeOf<InvalidDataException>().With.Message.Contains("complex"));
    }
}
