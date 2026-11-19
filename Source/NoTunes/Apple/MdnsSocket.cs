using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NoTunes.Apple;

internal sealed class MdnsSocket : IMdnsChannel
{
    private const int MulticastHopLimit = 255;
    private const int MaximumChannels = 32;
    private static readonly IPAddress Ipv4Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress Ipv6Group = IPAddress.Parse("ff02::fb");
    private readonly UdpClient client;
    private readonly IPEndPoint destination;

    private MdnsSocket(IPAddress address, int interfaceIndex)
    {
        this.InterfaceIndex = interfaceIndex;
        this.client = new UdpClient(address.AddressFamily);
        try
        {
            // Legacy mDNS queries request unicast replies to an ephemeral port, so Bonjour can keep port 5353.
            this.client.Client.Bind(new IPEndPoint(address, 0));
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                this.client.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    address.GetAddressBytes());

                this.client.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastTimeToLive,
                    MulticastHopLimit);

                this.destination = new IPEndPoint(Ipv4Group, MdnsPacket.Port);
            }
            else
            {
                this.client.Client.SetSocketOption(
                    SocketOptionLevel.IPv6,
                    SocketOptionName.MulticastInterface,
                    interfaceIndex);

                this.client.Client.SetSocketOption(
                    SocketOptionLevel.IPv6,
                    SocketOptionName.MulticastTimeToLive,
                    MulticastHopLimit);

                this.destination = new IPEndPoint(
                    new IPAddress(Ipv6Group.GetAddressBytes(), interfaceIndex), MdnsPacket.Port);
            }
        }
        catch
        {
            this.client.Dispose();
            throw;
        }
    }

    public long InterfaceIndex { get; }

    internal static IReadOnlyList<IMdnsChannel> Open()
    {
        var channels = new List<IMdnsChannel>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var properties = adapter.GetIPProperties();
                var ipv6Index = adapter.Supports(NetworkInterfaceComponent.IPv6)
                    ? properties.GetIPv6Properties().Index : 0;

                var addresses = properties.UnicastAddresses.Select(item => item.Address)
                    .Where(address => DeviceNetworkAddress.IsUnicast(address) && !IPAddress.IsLoopback(address))
                    .GroupBy(address => address.AddressFamily)
                    .Select(group => group.OrderByDescending(address => address.IsIPv6LinkLocal).First());

                foreach (var address in addresses)
                {
                    if (channels.Count >= MaximumChannels) return channels;

                    try
                    {
                        channels.Add(new MdnsSocket(address, ipv6Index));
                    }
                    catch (SocketException)
                    {
                        // An interface may disappear or disallow multicast while enumerating the others.
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Continue with the remaining interfaces.
            }
        }

        return channels;
    }

    public async ValueTask SendAsync(byte[] query, CancellationToken cancellationToken)
    {
        await this.client.SendAsync(query, this.destination, cancellationToken);
    }

    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        return this.client.ReceiveAsync(cancellationToken);
    }

    public void Dispose()
    {
        this.client.Dispose();
    }
}
