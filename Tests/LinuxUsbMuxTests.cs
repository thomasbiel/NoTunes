using System.Buffers.Binary;
using System.Net.Sockets;
using NoTunes.Apple;
using NoTunes.Platform.Linux;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class LinuxUsbMuxTests
{
    [Test]
    public async Task Simulated_Usbmuxd_Supports_Discovery_Pairing_And_Device_Tunnels()
    {
        var path = Path.Combine(Path.GetTempPath(), "nt-mux-" + Guid.NewGuid().ToString("N")[..8]);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            var server = ServeAsync();
            var mux = new UsbMuxClient(new LinuxUsbMuxTransport(path), TimeSpan.FromSeconds(3));
            var device = (await mux.ListUsbDevicesAsync(deadline.Token)).Single();
            Assert.That(device.Udid, Is.EqualTo("00008030-0011223344556677"));
            Assert.That((await mux.ReadPairRecordAsync(device.Udid, deadline.Token)).Text("HostID"), Is.EqualTo("test-host"));
            Assert.That(await mux.ReadSystemBuidAsync(deadline.Token), Is.EqualTo("test-buid"));
            await using (var tunnel = await mux.ConnectDeviceAsync(device, 62078, deadline.Token))
            {
                await tunnel.WriteAsync(new byte[] { 1, 2, 3 }, deadline.Token);
                var echoed = new byte[3];
                await tunnel.ReadExactlyAsync(echoed, deadline.Token);
                Assert.That(echoed, Is.EqualTo(new byte[] { 1, 2, 3 }));
            }

            await server.WaitAsync(deadline.Token);
        }
        finally
        {
            await deadline.CancelAsync();
            listener.Dispose();
            File.Delete(path);
        }

        async Task ServeAsync()
        {
            foreach (var operation in new[]
            {
                "ListDevices",
                "ReadPairRecord",
                "ReadBUID",
                "Connect"
            }

            )
            {
                using var socket = await listener.AcceptAsync(deadline.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                var header = new byte[16];
                await stream.ReadExactlyAsync(header, deadline.Token);
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header) - 16);
                Assert.That(length, Is.InRange(1, 4096));
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, deadline.Token);
                var request = Plist.Parse(payload).AsDict();
                Assert.That(request.Text("MessageType"), Is.EqualTo(operation));
                var reply = operation switch
                {
                    "ListDevices" => Plist.Dict(("DeviceList", new object[] { Plist.Dict(("DeviceID", 7L), ("Properties", Plist.Dict(("ConnectionType", "USB"), ("SerialNumber", "000080300011223344556677"), ("ProductID", 4776L)))) })),
                    "ReadPairRecord" => Plist.Dict(("PairRecordData", Plist.ToXml(Plist.Dict(("HostID", "test-host"))))),
                    "ReadBUID" => Plist.Dict(("BUID", "test-buid")),
                    _ => Plist.Dict(("Number", 0L))
                };

                await stream.WriteAsync(ProtocolFrames.UsbMux(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), reply), deadline.Token);
                if (operation != "Connect")
                    continue;
                Assert.That(request.Number("DeviceID"), Is.EqualTo(7));
                Assert.That(request.Number("PortNumber"), Is.EqualTo(32498));
                var bytes = new byte[3];
                await stream.ReadExactlyAsync(bytes, deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
            }
        }
    }

    [Test]
    public async Task Missing_Daemon_Reports_Usbmuxd_And_Its_Socket()
    {
        var path = Path.Combine(Path.GetTempPath(), "nt-missing-" + Guid.NewGuid().ToString("N")[..8]);
        var mux = new UsbMuxClient(new LinuxUsbMuxTransport(path), TimeSpan.FromSeconds(1));
        await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("usbmuxd").And.Message.Contains(path));
    }

    [Test]
    public async Task Connection_Timeout_Is_An_Io_Error()
    {
        var mux = new UsbMuxClient(new SilentUsbMuxTransport(), TimeSpan.FromMilliseconds(100));
        await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("Timed out"));
    }

    [Test]
    public async Task Caller_Cancellation_Is_Preserved()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var mux = new UsbMuxClient(new SilentUsbMuxTransport(), TimeSpan.FromSeconds(1));
        await Assert.ThatAsync(async () => await mux.ListUsbDevicesAsync(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }
}
