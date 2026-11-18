namespace NoTunes.Apple;

public sealed class AtcClient : IAsyncDisposable
{
    private const int StartupMessageLimit = 24;
    private const long ProbeSession = 1;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private readonly TimeSpan keepAliveInterval;
    private readonly CancellationTokenSource keepAliveStop = new();
    private Task? keepAlive;
    private int disposed;

    internal PlistConnection Connection { get; }

    internal TimeSpan RequestTimeout { get; }

    internal bool SupportsGrappa { get; private set; }

    public AtcClient(Stream stream, TimeSpan timeout) : this(stream, timeout, KeepAliveInterval) { }

    internal AtcClient(Stream stream, TimeSpan timeout, TimeSpan keepAliveInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval, TimeSpan.Zero);
        this.RequestTimeout = timeout;
        this.keepAliveInterval = keepAliveInterval;
        this.Connection = new PlistConnection(stream, timeout, atc: true);
    }

    internal void StartKeepAlive(CancellationToken cancellationToken)
    {
        this.keepAlive ??= this.SendKeepAliveAsync(cancellationToken);
    }

    public async Task<AtcSyncStatus> ReadSyncStatusAsync(CancellationToken cancellationToken)
    {
        await this.Connection.SendAsync(
            Plist.Dict((AtcKeys.Command, AtcCommands.Ping), (AtcKeys.Session, ProbeSession)),
            cancellationToken);

        long? count = null;
        long? bytes = null;
        // Bound unsolicited startup frames rather than waiting indefinitely for a particular message.
        for (var i = 0; i < StartupMessageLimit; i++)
        {
            var message = await this.Connection.ReceiveAsync(cancellationToken);
            var command = message.Text(AtcKeys.Command);
            if (command == AtcCommands.Capabilities &&
                message.TryGetValue(AtcKeys.Parameters, out var capabilities) &&
                capabilities.AsDict().TryGetValue(AtcKeys.Capabilities, out var grappa))
            {
                var values = grappa.AsDict();
                this.SupportsGrappa = values.Number(AtcKeys.CapabilityVersion) == SyncProtocol.CapabilityVersion
                    && values.Number(AtcKeys.ProtocolVersion) == SyncProtocol.CapabilityVersion;
            }

            if (command == AtcCommands.Ping)
            {
                await this.Connection.SendAsync(
                    Plist.Dict((AtcKeys.Command, AtcCommands.Pong), (AtcKeys.Session, message.Number(AtcKeys.Session))),
                    cancellationToken);

                continue;
            }

            if (command is AtcCommands.SyncFailed or AtcCommands.ConnectionInvalid)
            {
                throw new IOException($"The iPhone music sync service returned {command}.");
            }

            if (command == AtcCommands.AssetMetrics)
            {
                var metrics = ReadMusicMetrics(message);
                count = metrics.TrackCount ?? count;
                bytes = metrics.MusicBytes ?? bytes;
            }

            if (command != AtcCommands.SyncAllowed) continue;
            if (!message.TryGetValue(AtcKeys.Parameters, out var data)) throw new InvalidDataException("Music sync permissions are missing.");

            var permissions = ReadSyncPermissions(data.AsDict());
            return new AtcSyncStatus
            {
                SyncAllowed = permissions.Allowed,
                DataProtected = permissions.DataProtected,
                MusicTrackCount = count,
                MusicBytes = bytes
            };
        }

        throw new IOException("The iPhone did not report its music sync permissions within the startup message limit.");
    }

    private static (long? TrackCount, long? MusicBytes) ReadMusicMetrics(Dictionary<string, object> message)
    {
        if (!message.TryGetValue(AtcKeys.Parameters, out var parameters) ||
            !parameters.AsDict().TryGetValue(AtcKeys.Media, out var media) ||
            !media.AsDict().TryGetValue(AtcKeys.Music, out var music))
        {
            return (null, null);
        }

        var values = music.AsDict();
        return (OptionalNumber(values, AtcKeys.TrackCount), OptionalNumber(values, AtcKeys.PhysicalSize));
    }

    private static long? OptionalNumber(Dictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var value) && value is long number ? number : null;
    }

    internal static (bool Allowed, bool DataProtected) ReadSyncPermissions(Dictionary<string, object> parameters)
    {
        var manualSync = ReadFlag(AtcKeys.ManualSync);
        var autoSync = ReadFlag(AtcKeys.AutoSync);
        var dataProtected = ReadFlag(AtcKeys.DataProtected);
        var musicDisabled = false;
        if (parameters.TryGetValue(AtcKeys.DisabledAssetTypes, out var disabled))
        {
            if (disabled is not object[] types || types.Any(type => type is not string))
            {
                throw new InvalidDataException("Music sync permission 'DisabledAssetTypes' must be an array of strings.");
            }

            musicDisabled = types.Any(type => StringComparer.Ordinal.Equals(type, AtcKeys.Music));
        }

        return ((manualSync || autoSync) && !dataProtected && !musicDisabled, dataProtected);
        bool ReadFlag(string key)
        {
            if (!parameters.TryGetValue(key, out var value)) return false;
            return value is bool flag ? flag : throw new InvalidDataException($"Music sync permission '{key}' must be a boolean.");
        }
    }

    private async Task SendKeepAliveAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.keepAliveStop.Token);
        using var timer = new PeriodicTimer(this.keepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await this.Connection.SendAsync(
                    Plist.Dict((AtcKeys.Command, AtcCommands.Ping), (AtcKeys.Session, SyncProtocol.Session)),
                    stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Cancellation stops the timer and any pending send.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // SendAsync has discarded the connection; the active protocol reader observes its failure.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;

        await this.keepAliveStop.CancelAsync();
        try
        {
            await this.Connection.DisposeAsync();
        }
        finally
        {
            try
            {
                if (this.keepAlive is not null) await this.keepAlive;
            }
            finally
            {
                this.keepAliveStop.Dispose();
            }
        }
    }
}
