namespace NoTunes.Apple;

public sealed record DeviceInfo(string Udid, string Name, string Model, string IosVersion, string BuildVersion);

public sealed class LockdownClient : IAsyncDisposable
{
    private readonly UsbMuxClient mux;
    private readonly UsbDevice device;
    private readonly TimeSpan timeout;
    private readonly PlistConnection connection;
    private PairingIdentity? identity;
    private string? sessionId;

    private LockdownClient(UsbMuxClient mux, UsbDevice device, TimeSpan timeout, Stream stream)
    {
        this.mux = mux;
        this.device = device;
        this.timeout = timeout;
        connection = new PlistConnection(stream, timeout);
    }

    public static async Task<LockdownClient> ConnectAsync(UsbMuxClient mux, UsbDevice device,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = await mux.ConnectDeviceAsync(device, 62078, cancellationToken);
        var client = new LockdownClient(mux, device, timeout, stream);
        try
        {
            var reply = await client.RequestAsync("QueryType", [], cancellationToken);
            if (reply.Text("Type") != "com.apple.mobile.lockdown")
                throw new IOException("The connected service is not Apple's lockdown service.");
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var value = await GetValueAsync(null, null, cancellationToken);
        var info = value.AsDict();
        var udid = info.Text("UniqueDeviceID");
        if (!Normalize(udid).Equals(Normalize(device.Udid), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The connected iPhone identity does not match the selected USB device.");
        return new DeviceInfo(udid, info.Text("DeviceName"), info.Text("ProductType"),
            info.Text("ProductVersion"), info.Text("BuildVersion"));
    }

    public async Task<object> GetValueAsync(string? domain, string? key, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object>();
        if (domain is not null) fields["Domain"] = domain;
        if (key is not null) fields["Key"] = key;
        var reply = await RequestAsync("GetValue", fields, cancellationToken);
        return reply.TryGetValue("Value", out var value) ? value
            : throw new InvalidDataException("The iPhone did not return the requested value.");
    }

    public async Task StartPairedSessionAsync(CancellationToken cancellationToken)
    {
        if (sessionId is not null) throw new InvalidOperationException("A paired session is already open.");
        var record = await mux.ReadPairRecordAsync(device.Udid, cancellationToken);
        identity = PairingIdentity.FromRecord(record);
        var buid = record.TryGetValue("SystemBUID", out var savedBuid) && savedBuid is string text
            ? text : await mux.ReadSystemBuidAsync(cancellationToken);
        var reply = await RequestAsync("StartSession", Plist.Dict(("HostID", record.Text("HostID")),
            ("SystemBUID", buid)), cancellationToken);
        sessionId = reply.Text("SessionID");
        if (reply.Flag("EnableSessionSSL"))
            connection.UpgradeStream(await EncryptAsync(connection.Stream, cancellationToken));
    }

    public async Task<Stream> OpenServiceAsync(string name, CancellationToken cancellationToken)
    {
        if (sessionId is null) throw new IOException("A trusted iPhone pairing session is required.");
        var reply = await RequestAsync("StartService", Plist.Dict(("Service", name)), cancellationToken);
        var port = reply.Number("Port");
        if (port < 1 || port > ushort.MaxValue) throw new InvalidDataException("The iPhone returned an invalid service port.");
        var stream = await mux.ConnectDeviceAsync(device, (ushort)port, cancellationToken);
        try
        {
            return reply.Flag("EnableServiceSSL") ? await EncryptAsync(stream, cancellationToken) : stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task<Dictionary<string, object>> RequestAsync(string request,
        Dictionary<string, object> fields, CancellationToken cancellationToken)
    {
        fields["Label"] = "NoTunes";
        fields["Request"] = request;
        var reply = await connection.ExchangeAsync(fields, cancellationToken);
        if (reply.TryGetValue("Error", out var error))
        {
            var detail = error as string ?? "UnknownError";
            throw new IOException($"iPhone rejected {request}: {detail}. Unlock the iPhone and trust this computer.");
        }
        if (reply.Text("Request") != request) throw new InvalidDataException("Unexpected lockdown response.");
        return reply;
    }

    private Task<ManagedTlsStream> EncryptAsync(Stream raw, CancellationToken cancellationToken) =>
        ManagedTlsStream.ConnectAsync(raw, identity ?? throw new IOException("Pairing credentials are not loaded."),
            timeout, cancellationToken);

    private static string Normalize(string udid) => udid.Replace("-", "", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        // Closing the lockdown socket also ends the session, including on cancellation.
        await connection.DisposeAsync();
        identity = null;
    }
}
