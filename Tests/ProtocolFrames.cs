using System.Buffers.Binary;
using NoTunes.Apple;

namespace NoTunes.Tests;

internal static class ProtocolFrames
{
    private const int UsbMuxHeaderBytes = 16;
    private const int UsbMuxVersionOffset = 4;
    private const int UsbMuxMessageTypeOffset = 8;
    private const int UsbMuxTagOffset = 12;
    private const uint UsbMuxVersion = 1;
    private const uint UsbMuxPlistMessageType = 8;
    private const int PlistHeaderBytes = sizeof(int);
    private const int AfcTotalLengthOffset = 8;
    private const int AfcThisLengthOffset = 16;
    internal const int AfcHeaderBytes = 40;
    internal const int AfcSequenceOffset = 24;
    internal const int AfcOperationOffset = 32;

    private static ReadOnlySpan<byte> AfcMagic => "CFA6LPAA"u8;

    public static byte[] UsbMux(uint tag, Dictionary<string, object> message)
    {
        var payload = Plist.ToXml(message);
        var packet = new byte[UsbMuxHeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(UsbMuxVersionOffset), UsbMuxVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(UsbMuxMessageTypeOffset), UsbMuxPlistMessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(UsbMuxTagOffset), tag);
        payload.CopyTo(packet, UsbMuxHeaderBytes);
        return packet;
    }

    public static byte[] PlistMessage(Dictionary<string, object> message, bool atc = false)
    {
        var payload = atc ? BinaryPlist.Write(message) : Plist.ToXml(message);
        var packet = new byte[PlistHeaderBytes + payload.Length];
        if (atc)
            BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        else
            BinaryPrimitives.WriteInt32BigEndian(packet, payload.Length);

        payload.CopyTo(packet, PlistHeaderBytes);
        return packet;
    }

    public static byte[] Afc(ulong sequence, ulong operation, ReadOnlySpan<byte> body)
    {
        var packet = new byte[AfcHeaderBytes + body.Length];
        AfcMagic.CopyTo(packet);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(AfcTotalLengthOffset), (ulong)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(AfcThisLengthOffset), (ulong)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(AfcSequenceOffset), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(AfcOperationOffset), operation);
        body.CopyTo(packet.AsSpan(AfcHeaderBytes));
        return packet;
    }
}
