using System.Buffers.Binary;
using System.Net;

namespace NoTunes.Apple;

internal static class DeviceNetworkAddress
{
    private const byte Ipv4Family = 2;
    private const byte Ipv6Family = 30;
    private const int Ipv4SocketBytes = 16;
    private const int Ipv6SocketBytes = 28;
    private const int FamilyOffset = 1;
    private const int Ipv4AddressOffset = 4;
    private const int Ipv6AddressOffset = 8;
    private const int ScopeOffset = 24;
    private const int Ipv4AddressBytes = 4;
    private const int Ipv6AddressBytes = 16;
    private const int FirstMulticastPrefix = 224;
    private const int LastMulticastPrefix = 239;

    public static string Read(Dictionary<string, object> properties)
    {
        if (!properties.TryGetValue(UsbMuxKeys.NetworkAddress, out var value) || value is not byte[] bytes)
        {
            throw new InvalidDataException("The network device has no socket address.");
        }

        // usbmuxd publishes BSD sockaddr data, including on Windows and Linux.
        var address = bytes.Length switch
        {
            >= Ipv4SocketBytes when bytes[0] == Ipv4SocketBytes && bytes[FamilyOffset] == Ipv4Family =>
                new IPAddress(bytes.AsSpan(Ipv4AddressOffset, Ipv4AddressBytes)),
            >= Ipv6SocketBytes when bytes[0] == Ipv6SocketBytes && bytes[FamilyOffset] == Ipv6Family =>
                new IPAddress(bytes.AsSpan(Ipv6AddressOffset, Ipv6AddressBytes),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(ScopeOffset))),
            _ => throw new InvalidDataException("The network device returned an invalid or unsupported socket address.")
        };

        if (!IsUnicast(address)) throw new InvalidDataException("The network device address must be unicast.");

        return address.ToString();
    }

    public static bool IsUnicast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.IsIPv4MappedToIPv6 ? IsUnicast(address.MapToIPv4())
            : !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
              && !address.Equals(IPAddress.Broadcast) && !address.IsIPv6Multicast
              && (bytes.Length != Ipv4AddressBytes || bytes[0] is < FirstMulticastPrefix or > LastMulticastPrefix);
    }
}
