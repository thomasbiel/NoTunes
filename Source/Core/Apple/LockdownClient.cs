namespace NoTunes.Apple;

public sealed record DeviceInfo(string Udid, string Name, string Model, string IosVersion, string BuildVersion);

public sealed class LockdownClient : IAsyncDisposable
{
    private const ushort LockdownPort = 62078;
    private const string UnknownError = "UnknownError";

    private static class Keys
    {
        public const string Type = "Type";
        public const string Udid = "UniqueDeviceID";
        public const string DeviceName = "DeviceName";
        public const string ProductType = "ProductType";
        public const string ProductVersion = "ProductVersion";
        public const string BuildVersion = "BuildVersion";
        public const string Domain = "Domain";
        public const string Key = "Key";
        public const string Value = "Value";
        public const string SystemBuid = "SystemBUID";
        public const string HostId = "HostID";
        public const string SessionId = "SessionID";
        public const string EnableSessionSsl = "EnableSessionSSL";
        public const string Service = "Service";
        public const string Port = "Port";
        public const string EnableServiceSsl = "EnableServiceSSL";
        public const string Label = "Label";
        public const string Request = "Request";
        public const string Error = "Error";
    }

    private static class Requests
    {
        public const string QueryType = "QueryType";
        public const string GetValue = "GetValue";
        public const string StartSession = "StartSession";
        public const string StartService = "StartService";
    }

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
        var stream = await mux.ConnectDeviceAsync(device, LockdownPort, cancellationToken);
        var client = new LockdownClient(mux, device, timeout, stream);
        try
        {
            var reply = await client.RequestAsync(Requests.QueryType, [], cancellationToken);
            if (reply.Text(Keys.Type) != DeviceServices.Lockdown)
                throw new IOException("The connected service is not Apple's lockdown service.");
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var value = await GetValueAsync(null, null, cancellationToken);
        var info = value.AsDict();
        var udid = info.Text(Keys.Udid);
        if (!StringComparer.OrdinalIgnoreCase.Equals(DeviceUdid.Normalize(udid), DeviceUdid.Normalize(device.Udid)))
            throw new IOException("The connected iPhone identity does not match the selected USB device.");
        return new DeviceInfo(udid, info.Text(Keys.DeviceName), info.Text(Keys.ProductType),
            info.Text(Keys.ProductVersion), info.Text(Keys.BuildVersion));
    }

    public async Task<object> GetValueAsync(string? domain, string? key, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object>();
        if (domain is not null) fields[Keys.Domain] = domain;
        if (key is not null) fields[Keys.Key] = key;
        var reply = await RequestAsync(Requests.GetValue, fields, cancellationToken);
        return reply.TryGetValue(Keys.Value, out var value) ? value
            : throw new InvalidDataException("The iPhone did not return the requested value.");
    }

    public async Task StartPairedSessionAsync(CancellationToken cancellationToken)
    {
        if (sessionId is not null) throw new InvalidOperationException("A paired session is already open.");
        var record = await mux.ReadPairRecordAsync(device.Udid, cancellationToken);
        identity = PairingIdentity.FromRecord(record);
        var buid = record.TryGetValue(Keys.SystemBuid, out var savedBuid) && savedBuid is string text
            ? text : await mux.ReadSystemBuidAsync(cancellationToken);
        var reply = await RequestAsync(Requests.StartSession, Plist.Dict((Keys.HostId, record.Text(Keys.HostId)),
            (Keys.SystemBuid, buid)), cancellationToken);
        sessionId = reply.Text(Keys.SessionId);
        if (reply.Flag(Keys.EnableSessionSsl))
            connection.UpgradeStream(await EncryptAsync(connection.Stream, cancellationToken));
    }

    public async Task<Stream> OpenServiceAsync(string name, CancellationToken cancellationToken)
    {
        if (sessionId is null) throw new IOException("A trusted iPhone pairing session is required.");
        var reply = await RequestAsync(Requests.StartService, Plist.Dict((Keys.Service, name)), cancellationToken);
        var port = reply.Number(Keys.Port);
        if (port < 1 || port > ushort.MaxValue) throw new InvalidDataException("The iPhone returned an invalid service port.");
        var stream = await mux.ConnectDeviceAsync(device, (ushort)port, cancellationToken);
        try
        {
            return reply.Flag(Keys.EnableServiceSsl) ? await EncryptAsync(stream, cancellationToken) : stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task<Dictionary<string, object>> RequestAsync(string request,
        Dictionary<string, object> fields, CancellationToken cancellationToken)
    {
        fields[Keys.Label] = AppleClient.Name;
        fields[Keys.Request] = request;
        var reply = await connection.ExchangeAsync(fields, cancellationToken);
        if (reply.TryGetValue(Keys.Error, out var error))
        {
            var detail = error as string ?? UnknownError;
            throw new IOException($"iPhone rejected {request}: {detail}. Unlock the iPhone and trust this computer.");
        }
        if (reply.Text(Keys.Request) != request) throw new InvalidDataException("Unexpected lockdown response.");
        return reply;
    }

    private Task<ManagedTlsStream> EncryptAsync(Stream raw, CancellationToken cancellationToken) =>
        ManagedTlsStream.ConnectAsync(raw, identity ?? throw new IOException("Pairing credentials are not loaded."),
            timeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Closing the lockdown socket also ends the session, including on cancellation.
        await connection.DisposeAsync();
        identity = null;
    }
}
