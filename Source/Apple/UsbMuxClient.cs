using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NoTunes.Apple;

public sealed record UsbDevice(uint DeviceId, string Udid, long ProductId);

// Windows Apple Mobile Device Service exposes usbmux on loopback TCP port 27015.
// No Apple binaries are loaded into our process and no files are mounted as a drive.
public sealed class UsbMuxClient
{
    private readonly IPEndPoint endpoint;
    private readonly TimeSpan timeout;
    private int nextTag;

    public UsbMuxClient(TimeSpan timeout, IPEndPoint? endpoint = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
        this.endpoint = endpoint ?? new IPEndPoint(IPAddress.Loopback, 27015);
        if (!IPAddress.IsLoopback(this.endpoint.Address))
            throw new ArgumentException("The USB multiplexer endpoint must be a loopback address.", nameof(endpoint));
    }

    public async Task<IReadOnlyList<UsbDevice>> ListUsbDevicesAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(Plist.Dict(("MessageType", "ListDevices")), cancellationToken);
        CheckResult(reply, "list devices");
        if (!reply.TryGetValue("DeviceList", out var value) || value is not object[] devices)
            throw new InvalidDataException("The USB service did not return a device list.");
        var result = new List<UsbDevice>();
        foreach (var entry in devices)
        {
            var record = entry.AsDict();
            if (!record.TryGetValue("Properties", out var propertiesValue))
                throw new InvalidDataException("USB device properties are missing.");
            var properties = propertiesValue.AsDict();
            if (properties.Text("ConnectionType") != "USB") continue;
            var id = record.Number("DeviceID");
            if (id < 1 || id > uint.MaxValue) throw new InvalidDataException("Invalid USB device ID.");
            var udid = properties.Text("SerialNumber");
            if (udid.Length == 24) udid = udid.Insert(8, "-");
            result.Add(new UsbDevice((uint)id, udid, properties.Number("ProductID")));
        }
        return result.OrderBy(d => d.Udid, StringComparer.Ordinal).ToArray();
    }

    public async Task<Dictionary<string, object>> ReadPairRecordAsync(string udid, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(Plist.Dict(("MessageType", "ReadPairRecord"), ("PairRecordID", udid)), cancellationToken);
        CheckResult(reply, "read pairing record; unlock the iPhone and trust this computer first");
        if (!reply.TryGetValue("PairRecordData", out var value) || value is not byte[] bytes)
            throw new IOException("No pairing record is available. Unlock the iPhone and trust this computer in Apple Devices first.");
        // Pairing keys never leave this process and must never be included in diagnostics.
        return Plist.Parse(bytes).AsDict();
    }

    public async Task<string> ReadSystemBuidAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(Plist.Dict(("MessageType", "ReadBUID")), cancellationToken);
        CheckResult(reply, "read the system pairing identity");
        return reply.Text("BUID");
    }

    public async Task<NetworkStream> ConnectDeviceAsync(UsbDevice device, ushort port, CancellationToken cancellationToken)
    {
        var stream = await ConnectAsync(cancellationToken);
        try
        {
            // usbmux wraps the device port in a plist integer in network byte order.
            var networkPort = BinaryPrimitives.ReverseEndianness(port);
            var reply = await ExchangeAsync(stream, Plist.Dict(("MessageType", "Connect"),
                ("DeviceID", (long)device.DeviceId), ("PortNumber", (long)networkPort)), cancellationToken);
            if (!reply.TryGetValue("Number", out var status) || status is not long)
                throw new InvalidDataException("The USB service did not acknowledge the device connection.");
            CheckResult(reply, $"connect to iPhone port {port}");
            return stream; // From now on the connection carries the device service, not usbmux packets.
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private async Task<Dictionary<string, object>> RequestAsync(Dictionary<string, object> request, CancellationToken cancellationToken)
    {
        await using var stream = await ConnectAsync(cancellationToken);
        return await ExchangeAsync(stream, request, cancellationToken);
    }

    private async Task<NetworkStream> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await socket.ConnectAsync(endpoint, deadline.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException("Timed out connecting to Apple Mobile Device Service.", e);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new IOException("Cannot connect to Apple Mobile Device Service (127.0.0.1:27015). " +
                "Install Apple device support, start its service and reconnect the unlocked iPhone over USB.", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<Dictionary<string, object>> ExchangeAsync(NetworkStream stream,
        Dictionary<string, object> request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            request["ClientVersionString"] = "NoTunes/0.1";
            request["ProgName"] = "NoTunes";
            request["kLibUSBMuxVersion"] = 3L;
            var payload = Plist.ToXml(request);
            var tag = unchecked((uint)Interlocked.Increment(ref nextTag));
            var packet = new byte[16 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tag);
            payload.CopyTo(packet, 16);
            await stream.WriteAsync(packet, deadline.Token);
            var header = new byte[16];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (size < 16 || size > Plist.MaxBytes + 16
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 8
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != tag)
                throw new InvalidDataException("Invalid USB multiplexer response header.");
            var response = new byte[checked((int)size - 16)];
            await stream.ReadExactlyAsync(response, deadline.Token);
            return Plist.Parse(response).AsDict();
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out waiting for Apple Mobile Device Service.", e);
        }
    }

    private static void CheckResult(Dictionary<string, object> reply, string operation)
    {
        if (!reply.TryGetValue("Number", out var result)) return;
        if (result is not long code) throw new InvalidDataException("Invalid USB service result code.");
        if (code != 0) throw new IOException($"Apple USB service could not {operation} (error {code}).");
    }
}
