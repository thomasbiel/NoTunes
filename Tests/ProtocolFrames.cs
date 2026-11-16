using System.Buffers.Binary;
using NoTunes.Apple;

namespace NoTunes.Tests;

internal static class ProtocolFrames
{
    public static byte[] UsbMux(uint tag, Dictionary<string, object> message)
    {
        var payload = Plist.ToXml(message);
        var packet = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tag);
        payload.CopyTo(packet, 16);
        return packet;
    }

    public static byte[] PlistMessage(Dictionary<string, object> message, bool atc = false)
    {
        var payload = atc ? BinaryPlist.Write(message) : Plist.ToXml(message);
        var packet = new byte[4 + payload.Length];
        if (atc)
            BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        else
            BinaryPrimitives.WriteInt32BigEndian(packet, payload.Length);
        payload.CopyTo(packet, 4);
        return packet;
    }

    public static byte[] Afc(ulong sequence, ulong operation, ReadOnlySpan<byte> body)
    {
        var packet = new byte[40 + body.Length];
        "CFA6LPAA"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), (ulong)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(32), operation);
        body.CopyTo(packet.AsSpan(40));
        return packet;
    }
}
