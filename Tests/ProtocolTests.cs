using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NoTunes.Apple;
using NoTunes.Platform.Windows;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ProtocolTests
{
    [Test]
    public async Task Concurrent_Writers_Preserve_Complete_Plist_Frames()
    {
        await using var stream = new PausingWriteStream();
        await using var connection = new PlistConnection(stream, TimeSpan.FromSeconds(3), atc: true);
        var ping = Plist.Dict((AtcKeys.Command, AtcCommands.Ping), (AtcKeys.Session, SyncProtocol.Session));
        var pong = Plist.Dict((AtcKeys.Command, AtcCommands.Pong), (AtcKeys.Session, SyncProtocol.Session));
        var first = connection.SendAsync(ping, CancellationToken.None);
        await stream.FirstWriteStarted.Task;
        var second = connection.SendAsync(pong, CancellationToken.None);
        stream.ResumeFirstWrite.TrySetResult();
        await Task.WhenAll(first, second);
        byte[] expected =
        [
            .. ProtocolFrames.PlistMessage(ping, atc: true),
            .. ProtocolFrames.PlistMessage(pong, atc: true)
        ];

        Assert.That(stream.Written, Is.EqualTo(expected));
    }

    [Test]
    public async Task Lists_Only_Usb_Devices_And_Normalizes_Modern_Udids()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, request) = await ReadMuxRequest(stream);
            Assert.That(request.Text("MessageType"), Is.EqualTo("ListDevices"));
            await Reply(stream, tag, Plist.Dict(("DeviceList", new object[] { Plist.Dict(("DeviceID", 7L), ("Properties", Plist.Dict(("ConnectionType", "USB"), ("SerialNumber", "000080300011223344556677"), ("ProductID", 4776L)))), Plist.Dict(("DeviceID", 8L), ("Properties", Plist.Dict(("ConnectionType", "Network"), ("SerialNumber", "wifi"), ("ProductID", 4776L)))) })));
        }, async endpoint =>
        {
            var devices = await new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3)).ListUsbDevicesAsync(CancellationToken.None);
            Assert.That(devices, Has.Count.EqualTo(1));
            Assert.That(devices[0].DeviceId, Is.EqualTo(7));
            Assert.That(devices[0].Udid, Is.EqualTo("00008030-0011223344556677"));
        });
    }

    [Test]
    public async Task Connect_Uses_Network_Byte_Order_And_Switches_To_The_Raw_Device_Stream()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, request) = await ReadMuxRequest(stream);
            Assert.That(request.Text("MessageType"), Is.EqualTo("Connect"));
            Assert.That(request.Number("DeviceID"), Is.EqualTo(7));
            // 62078 = 0xF27E; the plist must contain 0x7EF2 = 32498.
            Assert.That(request.Number("PortNumber"), Is.EqualTo(32498));
            await Reply(stream, tag, Plist.Dict(("Number", 0L)));
            var payload = new byte[3];
            await stream.ReadExactlyAsync(payload);
            Assert.That(payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }, async endpoint =>
        {
            var mux = new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3));
            await using var stream = await mux.ConnectDeviceAsync(new UsbDevice(7, "test", 1), 62078, CancellationToken.None);
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
        });
    }

    [Test]
    public async Task Rejects_Usb_Replies_For_Another_Request()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, _) = await ReadMuxRequest(stream);
            await Reply(stream, tag + 1, Plist.Dict(("DeviceList", Array.Empty<object>())));
        }, async endpoint =>
        {
            var mux = new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3));
            await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        });
    }

    [Test]
    public async Task Propagates_Usb_Service_Errors_Rather_Than_Reporting_No_Devices()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, _) = await ReadMuxRequest(stream);
            await Reply(stream, tag, Plist.Dict(("Number", 3L)));
        }, async endpoint =>
        {
            var mux = new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3));
            await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("error 3"));
        });
    }

    [Test]
    public async Task Lockdown_And_Atc_Use_Their_Respective_Byte_Order([Values] bool atc)
    {
        var input = ProtocolFrames.PlistMessage(atc ? Plist.Dict(("Command", "SyncAllowed")) : Plist.Dict(("Request", "QueryType")), atc);
        await using var stream = new ScriptedStream(input);
        await using var connection = new PlistConnection(stream, TimeSpan.FromSeconds(3), atc);
        var result = await connection.ReceiveAsync(CancellationToken.None);
        Assert.That(result.Text(atc ? "Command" : "Request"), Is.EqualTo(atc ? "SyncAllowed" : "QueryType"));
        await connection.SendAsync(Plist.Dict(("Command", "Ping"), ("Session", 1L)), CancellationToken.None);
        var written = stream.Written.ToArray();
        var size = atc ? BinaryPrimitives.ReadInt32LittleEndian(written) : BinaryPrimitives.ReadInt32BigEndian(written);
        Assert.That(size, Is.EqualTo(written.Length - 4));
        Assert.That(Plist.Parse(written[4..]).AsDict().Text("Command"), Is.EqualTo("Ping"));
    }

    [Test]
    public async Task Damaged_Frame_Permanently_Closes_The_Connection()
    {
        await using var stream = new ScriptedStream([0x7f, 0xff, 0xff, 0xff]);
        await using var connection = new PlistConnection(stream, TimeSpan.FromSeconds(3));
        await Assert.ThatAsync(async () => await connection.ReceiveAsync(CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        await Assert.ThatAsync(async () => await connection.ReceiveAsync(CancellationToken.None), Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task Afc_Only_Treats_Object_Not_Found_As_Missing()
    {
        foreach (var code in new ulong[]
        {
            8,
            10
        }

        )
        {
            var status = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(status, code);
            var input = ProtocolFrames.Afc(1, 1, status);
            await using var afc = new AfcClient(new ScriptedStream(input), TimeSpan.FromSeconds(3));
            if (code == 8)
                Assert.That(await afc.GetFileInfoAsync("test", CancellationToken.None), Is.Null);
            else
                await Assert.ThatAsync(async () => await afc.GetFileInfoAsync("test", CancellationToken.None), Throws.TypeOf<AfcException>());
        }
    }

    [TestCase("first\0value\0last\0\0", "last", "")]
    [TestCase("first\0\0last\0value\0", "first", "")]
    [TestCase("key\0value\0", "key", "value")]
    public async Task Afc_Preserves_Empty_Field_Values(string fields, string key, string expected)
    {
        var frame = ProtocolFrames.Afc(1, 2, Encoding.UTF8.GetBytes(fields));
        await using var afc = new AfcClient(new ScriptedStream(frame), TimeSpan.FromSeconds(3));
        var result = await afc.GetDeviceInfoAsync(CancellationToken.None);
        Assert.That(result[key], Is.EqualTo(expected));
    }

    private static async Task WithMuxServer(Func<NetworkStream, Task> server, Func<IPEndPoint, Task> client)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = RunServer();
        try
        {
            await Task.WhenAll(client((IPEndPoint)listener.LocalEndpoint), serve).WaitAsync(cancellation.Token);
        }
        finally
        {
            listener.Stop();
        }

        async Task RunServer()
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
            await server(socket.GetStream());
        }
    }

    private static async Task<(uint Tag, Dictionary<string, object> Request)> ReadMuxRequest(Stream stream)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), Is.EqualTo(1));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), Is.EqualTo(8));
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header) - 16);
        Assert.That(size, Is.InRange(1, 4096));
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), Plist.Parse(payload).AsDict());
    }

    private static async Task Reply(Stream stream, uint tag, Dictionary<string, object> message)
    {
        var packet = ProtocolFrames.UsbMux(tag, message);
        await stream.WriteAsync(packet.AsMemory(0, 7));
        await stream.WriteAsync(packet.AsMemory(7));
    }
}
