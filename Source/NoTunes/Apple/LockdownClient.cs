namespace NoTunes.Apple;

public sealed class LockdownClient : IAsyncDisposable
{
    private const ushort LockdownPort = 62078;
    private const string UnknownError = "UnknownError";
    private readonly UsbMuxClient mux;
    private readonly UsbDevice device;
    private readonly TimeSpan timeout;
    private readonly PlistConnection connection;
    private PairingIdentity? identity;
    private string? sessionId;

    internal LockdownClient(UsbMuxClient mux, UsbDevice device, TimeSpan timeout, Stream stream)
    {
        this.mux = mux;
        this.device = device;
        this.timeout = timeout;
        this.connection = new PlistConnection(stream, timeout);
    }

    public static async Task<LockdownClient> ConnectAsync(UsbMuxClient mux, UsbDevice device, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = await mux.ConnectDeviceAsync(device, LockdownPort, cancellationToken);
        var client = new LockdownClient(
            mux,
            device,
            timeout,
            stream);

        try
        {
            var reply = await client.RequestAsync(LockdownRequests.QueryType, [], cancellationToken);
            if (reply.Text(LockdownKeys.Type) != DeviceServices.Lockdown)
            {
                throw new IOException("The connected service is not Apple's lockdown service.");
            }

            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var value = await this.GetValueAsync(null, null, cancellationToken);
        var info = value.AsDict();
        var udid = info.Text(LockdownKeys.Udid);
        if (!StringComparer.OrdinalIgnoreCase.Equals(DeviceUdid.Normalize(udid), DeviceUdid.Normalize(this.device.Udid)))
        {
            throw new IOException("The connected iPhone identity does not match the selected device.");
        }

        return new DeviceInfo(
            udid,
            info.Text(LockdownKeys.DeviceName),
            info.Text(LockdownKeys.ProductType),
            info.Text(LockdownKeys.ProductVersion),
            info.Text(LockdownKeys.BuildVersion));
    }

    private async Task<object> GetValueAsync(string? domain, string? key, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object>();
        if (domain is not null)
            fields[LockdownKeys.Domain] = domain;
        if (key is not null)
            fields[LockdownKeys.Key] = key;
        var reply = await this.RequestAsync(LockdownRequests.GetValue, fields, cancellationToken);
        return reply.TryGetValue(LockdownKeys.Value, out var value) ? value : throw new InvalidDataException("The iPhone did not return the requested value.");
    }

    public async Task StartPairedSessionAsync(CancellationToken cancellationToken)
    {
        if (this.sessionId is not null) throw new InvalidOperationException("A paired session is already open.");

        var record = await this.mux.ReadPairRecordAsync(this.device.Udid, cancellationToken);
        this.identity = PairingIdentity.FromRecord(record);
        var buid = record.TryGetValue(LockdownKeys.SystemBuid, out var savedBuid) && savedBuid is string text ? text : await this.mux.ReadSystemBuidAsync(cancellationToken);
        var reply = await this.RequestAsync(LockdownRequests.StartSession, Plist.Dict((LockdownKeys.HostId, record.Text(LockdownKeys.HostId)), (LockdownKeys.SystemBuid, buid)), cancellationToken);
        try
        {
            if (!reply.TryGetValue(LockdownKeys.EnableSessionSsl, out var ssl) || ssl is not true)
            {
                throw new InvalidDataException("EnableSessionSSL must be true to authenticate the paired iPhone.");
            }

            var id = reply.Text(LockdownKeys.SessionId);
            this.connection.UpgradeStream(await this.EncryptAsync(this.connection.Stream, cancellationToken));
            this.sessionId = id;
        }
        catch
        {
            await this.connection.DisposeAsync();
            this.identity = null;
            throw;
        }
    }

    public async Task<Stream> OpenServiceAsync(string name, CancellationToken cancellationToken)
    {
        if (this.sessionId is null) throw new IOException("A trusted iPhone pairing session is required.");

        var reply = await this.RequestAsync(LockdownRequests.StartService, Plist.Dict((LockdownKeys.Service, name)), cancellationToken);
        if (this.device.NetworkAddress is not null && !reply.Flag(LockdownKeys.EnableServiceSsl))
        {
            throw new IOException($"The iPhone did not enable TLS for Wi-Fi service '{name}'.");
        }

        var port = reply.Number(LockdownKeys.Port);
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException("The iPhone returned an invalid service port.");
        }

        var stream = await this.mux.ConnectDeviceAsync(this.device, (ushort)port, cancellationToken);
        try
        {
            return reply.Flag(LockdownKeys.EnableServiceSsl) ? await this.EncryptAsync(stream, cancellationToken) : stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task EnableWifiSyncAsync(CancellationToken cancellationToken)
    {
        if (this.sessionId is null) throw new IOException("A trusted iPhone pairing session is required.");
        if (this.device.NetworkAddress is not null) throw new IOException("Enable Wi-Fi synchronization over USB first.");

        var supported = await this.GetValueAsync(LockdownKeys.WirelessDomain, LockdownKeys.SupportsWifiSyncing,
            cancellationToken);

        if (supported is not true) throw new IOException("This iPhone does not support Wi-Fi synchronization.");

        await this.RequestAsync(LockdownRequests.SetValue, Plist.Dict(
            (LockdownKeys.Domain, LockdownKeys.WirelessDomain),
            (LockdownKeys.Key, LockdownKeys.EnableWifiConnections),
            (LockdownKeys.Value, true)), cancellationToken);

        var enabled = await this.GetValueAsync(LockdownKeys.WirelessDomain, LockdownKeys.EnableWifiConnections,
            cancellationToken);

        if (enabled is not true) throw new IOException("The iPhone did not retain the Wi-Fi synchronization setting.");
    }

    private async Task<Dictionary<string, object>> RequestAsync(string request, Dictionary<string, object> fields, CancellationToken cancellationToken)
    {
        fields[LockdownKeys.Label] = AppleClient.Name;
        fields[LockdownKeys.Request] = request;
        var reply = await this.connection.ExchangeAsync(fields, cancellationToken);
        if (reply.TryGetValue(LockdownKeys.Error, out var error))
        {
            var detail = error as string ?? UnknownError;
            throw new IOException($"iPhone rejected {request}: {detail}. Unlock the iPhone and trust this computer.");
        }

        if (reply.Text(LockdownKeys.Request) != request)
        {
            throw new InvalidDataException("Unexpected lockdown response.");
        }

        return reply;
    }

    private Task<ManagedTlsStream> EncryptAsync(Stream raw, CancellationToken cancellationToken)
    {
        return ManagedTlsStream.ConnectAsync(
            raw,
            this.identity ?? throw new IOException("Pairing credentials are not loaded."),
            this.timeout,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the lockdown socket also ends the session, including on cancellation.
        await this.connection.DisposeAsync();
        this.identity = null;
    }
}
