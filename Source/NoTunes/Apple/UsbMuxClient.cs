using System.Buffers.Binary;
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
    private readonly IUsbMuxTransport transport;
    private readonly TimeSpan timeout;
    private int nextTag;

    public UsbMuxClient(IUsbMuxTransport transport, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        this.timeout = timeout;
        this.transport = transport;
    }

    public async Task<IReadOnlyList<UsbDevice>> ListUsbDevicesAsync(CancellationToken cancellationToken)
    {
        var reply = await this.RequestAsync(Plist.Dict((UsbMuxKeys.MessageType, UsbMuxMessages.ListDevices)), cancellationToken);
        CheckResult(reply, "list devices");
        if (!reply.TryGetValue(UsbMuxKeys.DeviceList, out var value) || value is not object[] devices)
            throw new InvalidDataException("The USB service did not return a device list.");

        var result = new List<UsbDevice>();
        foreach (var entry in devices)
        {
            var record = entry.AsDict();
            if (!record.TryGetValue(UsbMuxKeys.Properties, out var propertiesValue))
                throw new InvalidDataException("USB device properties are missing.");

            var properties = propertiesValue.AsDict();
            if (properties.Text(UsbMuxKeys.ConnectionType) != UsbConnectionType)
                continue;
            var id = record.Number(UsbMuxKeys.DeviceId);
            if (id < 1 || id > uint.MaxValue) throw new InvalidDataException("Invalid USB device ID.");

            var udid = properties.Text(UsbMuxKeys.SerialNumber);
            if (udid.Length == DeviceUdid.ModernCompactLength)
                udid = udid.Insert(DeviceUdid.ModernPrefixLength, DeviceUdid.Separator);
            result.Add(new UsbDevice((uint)id, udid, properties.Number(UsbMuxKeys.ProductId)));
        }

        return result.OrderBy(d => d.Udid, StringComparer.Ordinal).ToArray();
    }

    public async Task<Dictionary<string, object>> ReadPairRecordAsync(string udid, CancellationToken cancellationToken)
    {
        var reply = await this.RequestAsync(Plist.Dict((UsbMuxKeys.MessageType, UsbMuxMessages.ReadPairRecord), (UsbMuxKeys.PairRecordId, udid)), cancellationToken);
        CheckResult(reply, "read pairing record; unlock the iPhone and trust this computer first");
        if (!reply.TryGetValue(UsbMuxKeys.PairRecordData, out var value) || value is not byte[] bytes)
            throw new IOException("No pairing record is available. Unlock the iPhone and establish trust with this computer first.");

        // Pairing keys never leave this process and must never be included in diagnostics.
        return Plist.Parse(bytes).AsDict();
    }

    public async Task<string> ReadSystemBuidAsync(CancellationToken cancellationToken)
    {
        var reply = await this.RequestAsync(Plist.Dict((UsbMuxKeys.MessageType, UsbMuxMessages.ReadBuid)), cancellationToken);
        CheckResult(reply, "read the system pairing identity");
        return reply.Text(UsbMuxKeys.Buid);
    }

    public async Task<Stream> ConnectDeviceAsync(UsbDevice device, ushort port, CancellationToken cancellationToken)
    {
        var stream = await this.ConnectAsync(cancellationToken);
        try
        {
            // usbmux wraps the device port in a plist integer in network byte order.
            var networkPort = BinaryPrimitives.ReverseEndianness(port);
            var reply = await this.ExchangeAsync(stream, Plist.Dict((UsbMuxKeys.MessageType, UsbMuxMessages.Connect), (UsbMuxKeys.DeviceId, (long)device.DeviceId), (UsbMuxKeys.PortNumber, (long)networkPort)), cancellationToken);
            if (!reply.TryGetValue(UsbMuxKeys.ResultNumber, out var status) || status is not long)
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

    private async Task<Dictionary<string, object>> ExchangeAsync(Stream stream, Dictionary<string, object> request, CancellationToken cancellationToken)
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
            if (size < HeaderBytes || size > Plist.MaxBytes + HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(VersionOffset)) != ProtocolVersion || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(MessageTypeOffset)) != PlistMessageType || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(TagOffset)) != tag)
                throw new InvalidDataException("Invalid USB multiplexer response header.");

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
        if (!reply.TryGetValue(UsbMuxKeys.ResultNumber, out var result))
            return;
        if (result is not long code) throw new InvalidDataException("Invalid USB service result code.");
        if (code != SuccessResultCode)
            throw new IOException($"Apple USB service could not {operation} (error {code}).");
    }
}
