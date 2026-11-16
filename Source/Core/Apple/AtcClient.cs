namespace NoTunes.Apple;

public sealed record AtcSyncStatus
{
    public required bool SyncAllowed { get; init; }
    public required bool DataProtected { get; init; }
    public required long? MusicTrackCount { get; init; }
    public required long? MusicBytes { get; init; }
}

public sealed class AtcClient(Stream stream, TimeSpan timeout) : IAsyncDisposable
{
    private const int StartupMessageLimit = 24;
    private const long ProbeSession = 1;
    private readonly PlistConnection connection = new(stream, timeout, atc: true);

    private static class Keys
    {
        public const string Command = "Command";
        public const string Session = "Session";
        public const string Parameters = "Params";
        public const string Media = "Media";
        public const string Music = "Music";
        public const string TrackCount = "_Count";
        public const string PhysicalSize = "_PhysicalSize";
        public const string DataProtected = "DataProtected";
        public const string DisabledAssetTypes = "DisabledAssetTypes";
        public const string ManualSync = "ManualSync";
        public const string AutoSync = "AutoSync";
    }

    private static class Commands
    {
        public const string Ping = "Ping";
        public const string Pong = "Pong";
        public const string SyncFailed = "SyncFailed";
        public const string ConnectionInvalid = "ConnectionInvalid";
        public const string AssetMetrics = "AssetMetrics";
        public const string SyncAllowed = "SyncAllowed";
    }

    public async Task<AtcSyncStatus> ReadSyncStatusAsync(CancellationToken cancellationToken)
    {
        await connection.SendAsync(Plist.Dict((Keys.Command, Commands.Ping), (Keys.Session, ProbeSession)), cancellationToken);
        long? count = null;
        long? bytes = null;
        // Bound unsolicited startup frames rather than waiting indefinitely for a particular message.
        for (var i = 0; i < StartupMessageLimit; i++)
        {
            var message = await connection.ReceiveAsync(cancellationToken);
            var command = message.Text(Keys.Command);
            if (command == Commands.Ping)
            {
                await connection.SendAsync(Plist.Dict((Keys.Command, Commands.Pong),
                    (Keys.Session, message.Number(Keys.Session))), cancellationToken);
                continue;
            }
            if (command is Commands.SyncFailed or Commands.ConnectionInvalid)
                throw new IOException($"The iPhone music sync service returned {command}.");
            if (command == Commands.AssetMetrics)
            {
                var metrics = ReadMusicMetrics(message);
                count = metrics.TrackCount ?? count;
                bytes = metrics.MusicBytes ?? bytes;
            }
            if (command != Commands.SyncAllowed) continue;
            var parameters = message.TryGetValue(Keys.Parameters, out var data) ? data.AsDict()
                : throw new InvalidDataException("Music sync permissions are missing.");
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
        if (!message.TryGetValue(Keys.Parameters, out var parameters)
            || !parameters.AsDict().TryGetValue(Keys.Media, out var media)
            || !media.AsDict().TryGetValue(Keys.Music, out var music))
            return (null, null);
        var values = music.AsDict();
        long? count = values.TryGetValue(Keys.TrackCount, out var number) && number is long value ? value : null;
        long? bytes = values.TryGetValue(Keys.PhysicalSize, out var size) && size is long length ? length : null;
        return (count, bytes);
    }

    internal static (bool Allowed, bool DataProtected) ReadSyncPermissions(Dictionary<string, object> parameters)
    {
        var manualSync = ReadFlag(Keys.ManualSync);
        var autoSync = ReadFlag(Keys.AutoSync);
        var dataProtected = ReadFlag(Keys.DataProtected);
        var musicDisabled = false;
        if (parameters.TryGetValue(Keys.DisabledAssetTypes, out var disabled))
        {
            if (disabled is not object[] types || types.Any(type => type is not string))
                throw new InvalidDataException("Music sync permission 'DisabledAssetTypes' must be an array of strings.");
            musicDisabled = types.Any(type => StringComparer.Ordinal.Equals(type, Keys.Music));
        }
        return ((manualSync || autoSync) && !dataProtected && !musicDisabled, dataProtected);

        bool ReadFlag(string key)
        {
            if (!parameters.TryGetValue(key, out var value)) return false;
            return value is bool flag ? flag
                : throw new InvalidDataException($"Music sync permission '{key}' must be a boolean.");
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
