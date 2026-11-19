using System.Buffers.Binary;
using System.Net;
using System.Text;
using NoTunes.Apple;

namespace NoTunes.Tests;

internal static class MdnsTestData
{
    internal const string Instance = "private-mac@phone._apple-mobdev2._tcp.local";
    internal const string Host = "phone.local";
    internal const uint DefaultTtl = 120;
    internal const int HeaderBytes = 12;
    private const ushort ResponseFlags = 0x8400;
    private const ushort InternetClass = 1;

    internal static byte[] Name(string name)
    {
        using var stream = new MemoryStream();
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        return stream.ToArray();
    }

    internal static byte[] Record(string name, MdnsRecordType type, byte[] data, uint ttl = DefaultTtl)
    {
        using var stream = new MemoryStream();
        stream.Write(Name(name));
        var header = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), InternetClass);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), checked((ushort)data.Length));
        stream.Write(header);
        stream.Write(data);
        return stream.ToArray();
    }

    internal static byte[] Ptr(uint ttl = DefaultTtl)
    {
        return Record(
            MdnsPacket.ServiceName,
            MdnsRecordType.Ptr,
            Name(Instance),
            ttl);
    }

    internal static byte[] Srv(ushort port = LockdownClient.LockdownPort)
    {
        var data = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port);
        return Record(Instance, MdnsRecordType.Srv, [.. data, .. Name(Host)]);
    }

    internal static byte[] Address(string address = "192.168.1.42")
    {
        var ip = IPAddress.Parse(address);
        var type = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? MdnsRecordType.A : MdnsRecordType.Aaaa;

        return Record(Host, type, ip.GetAddressBytes());
    }

    internal static byte[] Response(params byte[][] records)
    {
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt16BigEndian(header, MdnsPacket.QueryId);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), ResponseFlags);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), checked((ushort)records.Length));
        return [.. header, .. records.SelectMany(record => record)];
    }
}
