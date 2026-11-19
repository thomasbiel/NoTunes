using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NoTunes.Platform;

namespace NoTunes.Apple;

// usbmux framing is shared by Apple Mobile Device Service and Linux usbmuxd.
public sealed class UsbMuxClient
{
    private const int HeaderBytes = 4 * sizeof(uint);
    private const int VersionOffset = sizeof(uint);
    private const int MessageTypeOffset = 2 * sizeof(uint);
    private const int TagOffset = 3 * sizeof(uint);
    private const uint ProtocolVersion = 1;
    private const uint PlistMessageType = 8;
    private const long LibraryVersion = 3;
    private const long SuccessResultCode = 0;
    private const string UsbConnectionType = "USB";
    private const string NetworkConnectionType = "Network";
    private readonly IUsbMuxTransport transport;
    private readonly TimeSpan timeout;
    private int nextTag;

    internal Func<string, CancellationToken, Task<IReadOnlyList<UsbDevice>>>? BrowseBonjour { get; init; }

    public UsbMuxClient(IUsbMuxTransport transport, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        this.timeout = timeout;
        this.transport = transport;
    }

    public Task<IReadOnlyList<UsbDevice>> ListUsbDevicesAsync(CancellationToken cancellationToken)
    {
        return this.ListDevicesAsync(network: false, cancellationToken);
    }

    internal async Task<IReadOnlyList<UsbDevice>> FindDevicesAsync(
        bool network,
        string? udid,
        CancellationToken cancellationToken)
    {
        var devices = await this.ListDevicesAsync(network, cancellationToken);
        if (!network || string.IsNullOrWhiteSpace(udid) || devices.Any(device =>
            StringComparer.OrdinalIgnoreCase.Equals(DeviceUdid.Normalize(device.Udid), DeviceUdid.Normalize(udid))))
        {
            return devices;
        }

        var found = await (this.BrowseBonjour ?? new BonjourDeviceDiscovery(this, this.timeout).FindAsync)(
            udid, cancellationToken);

        return [.. devices, .. found];
    }

    public async Task<IReadOnlyList<UsbDevice>> ListDevicesAsync(bool network, CancellationToken cancellationToken)
    {
        var request = Plist.Dict((UsbMuxKeys.MessageType, UsbMuxMessages.ListDevices));
        var reply = await this.RequestAsync(request, cancellationToken);
        CheckResult(reply, "list devices");
        if (!reply.TryGetValue(UsbMuxKeys.DeviceList, out var value) || value is not object[] devices)
        {
            throw new InvalidDataException("The USB service did not return a device list.");
        }

        var result = new List<UsbDevice>();
        foreach (var entry in devices)
        {
            var record = entry.AsDict();
            if (!record.TryGetValue(UsbMuxKeys.Properties, out var propertiesValue))
            {
                throw new InvalidDataException("USB device properties are missing.");
            }

            var properties = propertiesValue.AsDict();
            var expectedType = network ? NetworkConnectionType : UsbConnectionType;
            if (properties.Text(UsbMuxKeys.ConnectionType) != expectedType) continue;

            var id = record.Number(UsbMuxKeys.DeviceId);
            if (id is < 1 or > uint.MaxValue) throw new InvalidDataException("Invalid USB device ID.");

            var udid = DeviceUdid.Format(properties.Text(UsbMuxKeys.SerialNumber));
            var productId = network && !properties.ContainsKey(UsbMuxKeys.ProductId)
                ? 0 : properties.Number(UsbMuxKeys.ProductId);

            result.Add(new UsbDevice((uint)id, udid, productId)
            {
                NetworkAddress = network ? DeviceNetworkAddress.Read(properties) : null
            });
        }

        return [.. result.OrderBy(d => d.Udid, StringComparer.Ordinal)];
    }

    public async Task<Dictionary<string, object>> ReadPairRecordAsync(string udid, CancellationToken cancellationToken)
    {
        var request = Plist.Dict(
            (UsbMuxKeys.MessageType, UsbMuxMessages.ReadPairRecord),
            (UsbMuxKeys.PairRecordId, udid));

        var reply = await this.RequestAsync(request, cancellationToken);
        CheckResult(reply, "read pairing record; unlock the iPhone and trust this computer first");
        if (!reply.TryGetValue(UsbMuxKeys.PairRecordData, out var value) || value is not byte[] bytes)
        {
            throw new IOException("No pairing record is available. Unlock the iPhone and establish trust with this computer first.");
        }

        // Pairing keys never leave this process and must never be included in diagnostics.
        return Plist.Parse(bytes).AsDict();
    }

    public async Task<string> ReadSystemBuidAsync(CancellationToken cancellationToken)
    {
        var request = Plist.Dict((UsbMuxKeys.MessageType, UsbMuxMessages.ReadBuid));
        var reply = await this.RequestAsync(request, cancellationToken);
        CheckResult(reply, "read the system pairing identity");
        return reply.Text(UsbMuxKeys.Buid);
    }

    public async Task<Stream> ConnectDeviceAsync(UsbDevice device, ushort port, CancellationToken cancellationToken)
    {
        if (device.NetworkAddress is not null)
        {
            return await this.ConnectNetworkDeviceAsync(device.NetworkAddress, port, cancellationToken);
        }

        var stream = await this.ConnectAsync(cancellationToken);
        try
        {
            // usbmux wraps the device port in a plist integer in network byte order.
            var networkPort = BinaryPrimitives.ReverseEndianness(port);
            var request = Plist.Dict(
                (UsbMuxKeys.MessageType, UsbMuxMessages.Connect),
                (UsbMuxKeys.DeviceId, (long)device.DeviceId),
                (UsbMuxKeys.PortNumber, (long)networkPort));

            var reply = await this.ExchangeAsync(stream, request, cancellationToken);
            if (!reply.TryGetValue(UsbMuxKeys.ResultNumber, out var status) || status is not long)
            {
                throw new InvalidDataException("The USB service did not acknowledge the device connection.");
            }

            CheckResult(reply, $"connect to iPhone port {port}");
            return stream; // From now on the connection carries the device service, not usbmux packets.
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private async Task<Stream> ConnectNetworkDeviceAsync(string address, ushort port, CancellationToken cancellationToken)
    {
        var ip = IPAddress.Parse(address);
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(this.timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), deadline.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException($"Timed out connecting to the iPhone over Wi-Fi ({address}:{port}). Unlock it and check the network.", e);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new IOException($"Cannot connect to the iPhone over Wi-Fi ({address}:{port}). Unlock it and check the network.", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<Dictionary<string, object>> RequestAsync(Dictionary<string, object> request, CancellationToken cancellationToken)
    {
        await using var stream = await this.ConnectAsync(cancellationToken);
        return await this.ExchangeAsync(stream, request, cancellationToken);
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(this.timeout);
        try
        {
            return await this.transport.ConnectAsync(deadline.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out connecting to the local USB multiplexer.", e);
        }
    }

    private async Task<Dictionary<string, object>> ExchangeAsync(
        Stream stream,
        Dictionary<string, object> request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(this.timeout);
        try
        {
            request[UsbMuxKeys.ClientVersion] = AppleClient.VersionString;
            request[UsbMuxKeys.ProgramName] = AppleClient.Name;
            request[UsbMuxKeys.LibraryVersion] = LibraryVersion;
            var payload = Plist.ToXml(request);
            var tag = unchecked((uint)Interlocked.Increment(ref this.nextTag));
            var packet = new byte[HeaderBytes + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(VersionOffset), ProtocolVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(MessageTypeOffset), PlistMessageType);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(TagOffset), tag);
            payload.CopyTo(packet, HeaderBytes);
            await stream.WriteAsync(packet, deadline.Token);
            var header = new byte[HeaderBytes];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(VersionOffset));
            var messageType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(MessageTypeOffset));
            var responseTag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(TagOffset));
            var validSize = size is >= HeaderBytes and <= Plist.MaxBytes + HeaderBytes;
            var expectedEnvelope = version == ProtocolVersion && messageType == PlistMessageType && responseTag == tag;
            if (!validSize || !expectedEnvelope) throw new InvalidDataException("Invalid USB multiplexer response header.");

            var response = new byte[checked((int)size - HeaderBytes)];
            await stream.ReadExactlyAsync(response, deadline.Token);
            return Plist.Parse(response).AsDict();
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out waiting for the local USB multiplexer.", e);
        }
    }

    private static void CheckResult(Dictionary<string, object> reply, string operation)
    {
        if (!reply.TryGetValue(UsbMuxKeys.ResultNumber, out var result)) return;
        if (result is not long code) throw new InvalidDataException("Invalid USB service result code.");
        if (code != SuccessResultCode) throw new IOException($"Apple USB service could not {operation} (error {code}).");
    }
}
