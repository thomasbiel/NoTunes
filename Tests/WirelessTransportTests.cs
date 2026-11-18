using System.Net;
using System.Net.Sockets;
using NoTunes.Apple;
using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class WirelessTransportTests
{
    [Test]
    public async Task Discovers_Only_The_Requested_Transport([Values] bool network)
    {
        var properties = Plist.Dict(
            ("ConnectionType", "Network"),
            ("SerialNumber", "000080300011223344556677"),
            ("NetworkAddress", SocketAddress("192.168.1.42")));

        var reply = Plist.Dict(("DeviceList", new object[]
        {
            Plist.Dict(("DeviceID", 7L), ("Properties", Plist.Dict(
                ("ConnectionType", "USB"), ("SerialNumber", "usb"), ("ProductID", 4776L)))),
            Plist.Dict(("DeviceID", 8L), ("Properties", properties))
        }));

        var mux = CreateMux(reply);
        var devices = await mux.ListDevicesAsync(network, CancellationToken.None);
        var device = devices.Single();
        Assert.That(device.DeviceId, Is.EqualTo(network ? 8 : 7));
        Assert.That(device.Udid, Is.EqualTo(network ? "00008030-0011223344556677" : "usb"));
        Assert.That(device.NetworkAddress, Is.EqualTo(network ? "192.168.1.42" : null));
        Assert.That(device.ProductId, Is.EqualTo(network ? 0 : 4776));
        Assert.That(device.ConnectionType, Is.EqualTo(network ? "Network" : "USB"));
    }

    [TestCase("192.168.1.42")]
    [TestCase("2001:db8::42")]
    [TestCase("fe80::1234%7")]
    public void Reads_Bsd_Network_Addresses_And_Ipv6_Scopes(string address)
    {
        var properties = Plist.Dict(("NetworkAddress", SocketAddress(address)));
        Assert.That(DeviceNetworkAddress.Read(properties), Is.EqualTo(address));
    }

    [TestCase("0.0.0.0")]
    [TestCase("255.255.255.255")]
    [TestCase("224.0.0.1")]
    [TestCase("::")]
    [TestCase("ff02::1")]
    [TestCase("::ffff:224.0.0.1")]
    public void Rejects_Non_Unicast_Network_Addresses(string address)
    {
        Assert.That(() => DeviceNetworkAddress.Read(Plist.Dict(("NetworkAddress", SocketAddress(address)))),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Rejects_Malformed_Network_Addresses()
    {
        foreach (var bytes in new byte[][] { [], [16], [16, 2], new byte[16], [28, 30, 0, 0] })
        {
            Assert.That(() => DeviceNetworkAddress.Read(Plist.Dict(("NetworkAddress", bytes))),
                Throws.TypeOf<InvalidDataException>());
        }

        Assert.That(() => DeviceNetworkAddress.Read([]), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Connects_Directly_To_The_Device_Service_Without_A_Mux_Tunnel([Values] bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var listener = new TcpListener(address, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new ReplyUsbMuxTransport();
        var mux = new UsbMuxClient(transport, TimeSpan.FromSeconds(2));
        var device = new UsbDevice(8, "wifi-phone", 0) { NetworkAddress = address.ToString() };
        var accept = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using var client = await mux.ConnectDeviceAsync(
            device, (ushort)((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);

        using var peer = await accept;
        await client.WriteAsync(new byte[] { 1, 2, 3 }, deadline.Token);
        var received = new byte[3];
        await peer.GetStream().ReadExactlyAsync(received, deadline.Token);
        Assert.That(received, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(transport.Connections, Is.Zero);
    }

    [Test]
    public async Task Preserves_Caller_Cancellation_When_Connecting_Over_Wifi()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var mux = new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(() => mux.ConnectDeviceAsync(
            new UsbDevice(1, "test", 0) { NetworkAddress = "127.0.0.1" }, 62078, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Reports_Refused_Wifi_Connections_As_Io_Errors()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var mux = new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(2));
        await Assert.ThatAsync(() => mux.ConnectDeviceAsync(
            new UsbDevice(1, "test", 0) { NetworkAddress = "127.0.0.1" }, port, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("Wi-Fi"));
    }

    [Test]
    public async Task Direct_Addresses_Use_The_Requested_Pairing_Without_Discovery()
    {
        var options = new ProbeOptions { Address = "192.168.1.42", Device = "000080300011223344556677", Timeout = 2 };
        var transport = new ReplyUsbMuxTransport();
        var device = await options.SelectDeviceAsync(new UsbMuxClient(transport, TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.That(device.Udid, Is.EqualTo("00008030-0011223344556677"));
        Assert.That(device.NetworkAddress, Is.EqualTo(options.Address));
        Assert.That(transport.Connections, Is.Zero);
    }

    [Test]
    public void Multiple_Addresses_For_One_Iphone_Do_Not_Require_Another_Device_Selection([Values] bool selectUdid)
    {
        UsbDevice[] devices =
        [
            new(1, "00008030-0011223344556677", 0) { NetworkAddress = "192.168.1.42" },
            new(2, "000080300011223344556677", 0) { NetworkAddress = "fe80::42%7" }
        ];

        Assert.That(DeviceOptions.SelectDevice(devices, selectUdid ? devices[0].Udid : null, network: true),
            Is.SameAs(devices[0]));
    }

    private static UsbMuxClient CreateMux(Dictionary<string, object> reply)
    {
        return new UsbMuxClient(new ReplyUsbMuxTransport(new ScriptedStream(ProtocolFrames.UsbMux(1, reply))),
            TimeSpan.FromSeconds(2));
    }

    private static byte[] SocketAddress(string address)
    {
        var ip = IPAddress.Parse(address);
        var ipv6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        var bytes = new byte[ipv6 ? 28 : 16];
        bytes[0] = (byte)bytes.Length;
        bytes[1] = (byte)(ipv6 ? 30 : 2);
        ip.GetAddressBytes().CopyTo(bytes, ipv6 ? 8 : 4);
        if (ipv6) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)ip.ScopeId);

        return bytes;
    }
}
