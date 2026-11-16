using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NoTunes.Apple;
using NoTunes.Platform.Windows;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class ProtocolTests
{
    [Test]
    public async Task ListsOnlyUsbDevicesAndNormalizesModernUdids()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, request) = await ReadMuxRequest(stream);
            Assert.That(request.Text("MessageType"), Is.EqualTo("ListDevices"));
            await Reply(stream, tag, Plist.Dict(("DeviceList", new object[]
            {
                Plist.Dict(("DeviceID", 7L), ("Properties", Plist.Dict(("ConnectionType", "USB"),
                    ("SerialNumber", "000080300011223344556677"), ("ProductID", 4776L)))),
                Plist.Dict(("DeviceID", 8L), ("Properties", Plist.Dict(("ConnectionType", "Network"),
                    ("SerialNumber", "wifi"), ("ProductID", 4776L))))
            })));
        }, async endpoint =>
        {
            var devices = await new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3)).ListUsbDevicesAsync(CancellationToken.None);
            Assert.That(devices, Has.Count.EqualTo(1));
            Assert.That(devices[0].DeviceId, Is.EqualTo(7));
            Assert.That(devices[0].Udid, Is.EqualTo("00008030-0011223344556677"));
        });
    }

    [Test]
    public async Task ConnectUsesNetworkByteOrderAndSwitchesToTheRawDeviceStream()
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
    public async Task RejectsUsbRepliesForAnotherRequest()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, _) = await ReadMuxRequest(stream);
            await Reply(stream, tag + 1, Plist.Dict(("DeviceList", Array.Empty<object>())));
        }, async endpoint =>
        {
            var mux = new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3));
            await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(CancellationToken.None),
                Throws.TypeOf<InvalidDataException>());
        });
    }

    [Test]
    public async Task PropagatesUsbServiceErrorsRatherThanReportingNoDevices()
    {
        await WithMuxServer(async stream =>
        {
            var (tag, _) = await ReadMuxRequest(stream);
            await Reply(stream, tag, Plist.Dict(("Number", 3L)));
        }, async endpoint =>
        {
            var mux = new UsbMuxClient(new WindowsUsbMuxTransport(endpoint), TimeSpan.FromSeconds(3));
            await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(CancellationToken.None),
                Throws.TypeOf<IOException>().With.Message.Contains("error 3"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LockdownAndAtcUseTheirRespectiveByteOrder(bool atc)
    {
        var payload = atc ? BinaryPlist.Write(Plist.Dict(("Command", "SyncAllowed")))
            : Plist.ToXml(Plist.Dict(("Request", "QueryType")));
        var input = new byte[4 + payload.Length];
        if (atc) BinaryPrimitives.WriteInt32LittleEndian(input, payload.Length);
        else BinaryPrimitives.WriteInt32BigEndian(input, payload.Length);
        payload.CopyTo(input, 4);
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
    public async Task DamagedFramePermanentlyClosesTheConnection()
    {
        await using var stream = new ScriptedStream([0x7f, 0xff, 0xff, 0xff]);
        await using var connection = new PlistConnection(stream, TimeSpan.FromSeconds(3));
        await Assert.ThatAsync(async () => await connection.ReceiveAsync(CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        await Assert.ThatAsync(async () => await connection.ReceiveAsync(CancellationToken.None), Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task AfcOnlyTreatsObjectNotFoundAsMissing()
    {
        foreach (var code in new ulong[] { 8, 10 })
        {
            var input = new byte[48];
            "CFA6LPAA"u8.CopyTo(input);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(8), 48);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), 48);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(40), code);
            await using var afc = new AfcClient(new ScriptedStream(input), TimeSpan.FromSeconds(3));
            if (code == 8) Assert.That(await afc.GetFileInfoAsync("test", CancellationToken.None), Is.Null);
            else await Assert.ThatAsync(async () => await afc.GetFileInfoAsync("test", CancellationToken.None),
                Throws.TypeOf<AfcException>());
        }
    }

    private static async Task WithMuxServer(Func<NetworkStream, Task> server, Func<IPEndPoint, Task> client)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = RunServer();
        try { await Task.WhenAll(client((IPEndPoint)listener.LocalEndpoint), serve).WaitAsync(cancellation.Token); }
        finally { listener.Stop(); }

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
        var payload = Plist.ToXml(message);
        var packet = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tag);
        payload.CopyTo(packet, 16);
        await stream.WriteAsync(packet.AsMemory(0, 7));
        await stream.WriteAsync(packet.AsMemory(7));
    }

    private sealed class ScriptedStream(byte[] input) : Stream
    {
        private readonly MemoryStream incoming = new(input, writable: false);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => incoming.Read(buffer, offset, Math.Min(count, 3));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            incoming.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Written.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { incoming.Dispose(); Written.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
