namespace NoTunes.Apple;

public sealed class AtcClient(Stream stream, TimeSpan timeout) : IAsyncDisposable
{
    private const int StartupMessageLimit = 24;
    private const long ProbeSession = 1;

    internal PlistConnection Connection { get; } = new(stream, timeout, atc: true);

    internal TimeSpan RequestTimeout => timeout;

    internal bool SupportsGrappa { get; private set; }

    public async Task<AtcSyncStatus> ReadSyncStatusAsync(CancellationToken cancellationToken)
    {
        await this.Connection.SendAsync(Plist.Dict((AtcKeys.Command, AtcCommands.Ping), (AtcKeys.Session, ProbeSession)), cancellationToken);
        long? count = null;
        long? bytes = null;
        // Bound unsolicited startup frames rather than waiting indefinitely for a particular message.
        for (var i = 0; i < StartupMessageLimit; i++)
        {
            var message = await this.Connection.ReceiveAsync(cancellationToken);
            var command = message.Text(AtcKeys.Command);
            if (command == AtcCommands.Capabilities && message.TryGetValue(AtcKeys.Parameters, out var capabilities)
                && capabilities.AsDict().TryGetValue(AtcKeys.Capabilities, out var grappa))
            {
                var values = grappa.AsDict();
                this.SupportsGrappa = values.Number(AtcKeys.CapabilityVersion) == SyncProtocol.CapabilityVersion
                    && values.Number(AtcKeys.ProtocolVersion) == SyncProtocol.CapabilityVersion;
            }

            if (command == AtcCommands.Ping)
            {
                await this.Connection.SendAsync(Plist.Dict((AtcKeys.Command, AtcCommands.Pong), (AtcKeys.Session, message.Number(AtcKeys.Session))), cancellationToken);
                continue;
            }

            if (command is AtcCommands.SyncFailed or AtcCommands.ConnectionInvalid)
                throw new IOException($"The iPhone music sync service returned {command}.");

            if (command == AtcCommands.AssetMetrics)
            {
                var metrics = ReadMusicMetrics(message);
                count = metrics.TrackCount ?? count;
                bytes = metrics.MusicBytes ?? bytes;
            }

            if (command != AtcCommands.SyncAllowed)
                continue;
            var parameters = message.TryGetValue(AtcKeys.Parameters, out var data) ? data.AsDict() : throw new InvalidDataException("Music sync permissions are missing.");
            var permissions = ReadSyncPermissions(parameters);
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
        if (!message.TryGetValue(AtcKeys.Parameters, out var parameters) || !parameters.AsDict().TryGetValue(AtcKeys.Media, out var media) || !media.AsDict().TryGetValue(AtcKeys.Music, out var music))
            return (null, null);
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
                throw new InvalidDataException("Music sync permission 'DisabledAssetTypes' must be an array of strings.");

            musicDisabled = types.Any(type => StringComparer.Ordinal.Equals(type, AtcKeys.Music));
        }

        return ((manualSync || autoSync) && !dataProtected && !musicDisabled, dataProtected);
        bool ReadFlag(string key)
        {
            if (!parameters.TryGetValue(key, out var value))
                return false;
            return value is bool flag ? flag : throw new InvalidDataException($"Music sync permission '{key}' must be a boolean.");
        }
    }

    public ValueTask DisposeAsync() => this.Connection.DisposeAsync();
}
