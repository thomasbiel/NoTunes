using System.Buffers.Binary;
using System.Net;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class MdnsPacketTests
{
    [Test]
    public void Encodes_A_Legacy_Unicast_Response_Query()
    {
        var query = MdnsPacket.Query(new MdnsQuestion(MdnsPacket.ServiceName, MdnsRecordType.Ptr));
        byte[] expectedHeader = [0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        byte[] expectedTrailer = [0, 12, 0, 1];
        byte[] expected = [.. expectedHeader, .. MdnsTestData.Name(MdnsPacket.ServiceName), .. expectedTrailer];
        Assert.That(query, Is.EqualTo(expected));
    }

    [Test]
    public void Reads_Compressed_Names_And_All_Response_Sections()
    {
        var ptr = MdnsTestData.Ptr();
        var srv = MdnsTestData.Srv();
        var packet = MdnsTestData.Response(ptr, srv, MdnsTestData.Address());
        // Place the SRV and address in the authority/additional sections instead of the answer section.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), 1);
        var serviceNameOffset = MdnsTestData.HeaderBytes;
        var instanceOffset = serviceNameOffset + MdnsTestData.Name(MdnsPacket.ServiceName).Length + 10;
        var srvOffset = MdnsTestData.HeaderBytes + ptr.Length;
        var srvNameLength = MdnsTestData.Name(MdnsTestData.Instance).Length;
        packet = [.. packet[..srvOffset], 0xC0, (byte)instanceOffset, .. packet[(srvOffset + srvNameLength)..]];

        var records = new MdnsPacket(packet).Read(interfaceIndex: 7);

        Assert.That(records, Has.Count.EqualTo(3));
        Assert.That(records[0].Target, Is.EqualTo(MdnsTestData.Instance));
        Assert.That(records[1].Name, Is.EqualTo(MdnsTestData.Instance));
        Assert.That(records[1].Target, Is.EqualTo(MdnsTestData.Host));
        Assert.That(records[1].Port, Is.EqualTo(LockdownClient.LockdownPort));
        Assert.That(records[2].Address, Is.EqualTo(IPAddress.Parse("192.168.1.42")));
    }

    [TestCase("192.168.1.42", "192.168.1.42")]
    [TestCase("2001:db8::42", "2001:db8::42")]
    [TestCase("fe80::42", "fe80::42%7")]
    public void Preserves_Ipv4_Ipv6_And_Link_Local_Scopes(string address, string expected)
    {
        var packet = MdnsTestData.Response(MdnsTestData.Address(address));
        var record = new MdnsPacket(packet).Read(interfaceIndex: 7).Single();
        Assert.That(record.Address, Is.EqualTo(IPAddress.Parse(expected)));
    }

    [Test]
    public void Rejects_Truncated_Packets_And_Compression_Loops()
    {
        var packet = MdnsTestData.Response(MdnsTestData.Ptr());
        for (var length = 0; length < packet.Length; length++)
        {
            var truncated = packet[..length];
            Assert.Throws<InvalidDataException>(() => new MdnsPacket(truncated).Read(7));
        }

        var loop = MdnsTestData.Response([0xC0, MdnsTestData.HeaderBytes]);
        Assert.Throws<InvalidDataException>(() => new MdnsPacket(loop).Read(7));
    }

    [TestCase(0, 2)]
    [TestCase(2, 0)]
    public void Ignores_Wrong_Transactions_And_Queries(int offset, byte value)
    {
        var packet = MdnsTestData.Response(MdnsTestData.Ptr());
        packet[offset] = value;
        Assert.That(new MdnsPacket(packet).Read(7), Is.Empty);
    }
}
