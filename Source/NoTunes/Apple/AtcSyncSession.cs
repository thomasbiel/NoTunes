using System.Globalization;
using NoTunes.Music;

namespace NoTunes.Apple;

internal sealed class AtcSyncSession(AtcClient client, TextWriter standardError)
{
    private readonly TextWriter error = standardError;
    private bool finishedMetadata;

    public async Task<GrappaSigner> BeginAsync(LibraryIdentity identity, uint anchor, CancellationToken cancellationToken)
    {
        if (!client.SupportsGrappa) throw new IOException("The iPhone did not advertise supported Grappa capabilities.");

        await this.SendAsync(AtcCommands.RequestingSync, Plist.Dict(
            (AtcKeys.Dataclasses, new object[] { AtcKeys.Media, AtcKeys.Book, AtcKeys.Keybag }),
            (AtcKeys.Anchors, Anchors(anchor)),
            (AtcKeys.HostInfo, Plist.Dict(
                (AtcKeys.HostType, SyncProtocol.HostType),
                (AtcKeys.HostVersion, SyncProtocol.HostVersion),
                (AtcKeys.WindowsVersion, SyncProtocol.WindowsVersion),
                (AtcKeys.HostName, Environment.MachineName),
                (AtcKeys.LibraryId, identity.LibraryId),
                (AtcKeys.SyncedDataclasses, new object[] { AtcKeys.Media }),
                (AtcKeys.SyncedAssetTypes, new object[] { AtcKeys.Music }),
                (AtcKeys.Wakeable, 0L),
                (AtcKeys.Grappa, GrappaSigner.HostRequest)))), cancellationToken);
        var ready = await this.WaitAsync([AtcCommands.ReadyForSync], cancellationToken);
        var parameters = ready.Dictionary(AtcKeys.Parameters);
        if (!StringComparer.Ordinal.Equals(parameters.Dictionary(AtcKeys.Anchors).Text(AtcKeys.Media), anchor.ToString(CultureInfo.InvariantCulture)))
            throw new IOException("The live Media anchor changed. Recompute the sync plan.");

        var token = parameters.Dictionary(AtcKeys.DeviceInfo).GetValueOrDefault(AtcKeys.Grappa) as byte[]
            ?? throw new InvalidDataException("ReadyForSync has no Grappa token.");
        return new GrappaSigner(token);
    }

    public async Task FinishMetadataAsync(uint anchor, CancellationToken cancellationToken)
    {
        if (this.finishedMetadata) throw new InvalidOperationException("Metadata has already been announced.");

        this.finishedMetadata = true;
        await this.SendAsync(AtcCommands.FinishedMetadata, Plist.Dict(
            (AtcKeys.SyncTypes, Plist.Dict((AtcKeys.Media, SyncProtocol.DeltaSync), (AtcKeys.Book, SyncProtocol.NoSync), (AtcKeys.Keybag, SyncProtocol.DeltaSync))),
            (AtcKeys.Anchors, Anchors(checked(anchor + 1)))), cancellationToken);
    }

    public async Task TransferAsync(
        AfcClient afc,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlySet<long> retained,
        CancellationToken cancellationToken)
    {
        if (!this.finishedMetadata) throw new InvalidOperationException("Metadata must be announced before assets.");

        var response = await this.WaitAsync([AtcKeys.AssetManifest, AtcCommands.SyncFinished], cancellationToken);
        if (response.Text(AtcKeys.Command) == AtcCommands.SyncFinished)
        {
            if (additions.Count != 0) throw new IOException("Sync finished without requesting the new audio files.");

            return;
        }

        // Validate the complete manifest before uploading or replying to any individual entry.
        var manifest = SyncManifest.Validate(response.Dictionary(AtcKeys.Parameters), additions, retained);
        foreach (var asset in manifest.Where(asset => asset.Track is not null))
        {
            var track = asset.Track!;
            var staged = SyncPaths.Airlock + track.AssetId;
            await this.error.WriteLineAsync($"Transfer: {track.Source.RelativePath}");
            await using var input = new FileStream(
                track.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (await afc.GetFileInfoAsync(track.DevicePath, cancellationToken) is not null
                || await afc.GetFileInfoAsync(staged, cancellationToken) is not null)
                throw new IOException("A planned audio destination is occupied.");

            await afc.WriteNewFileAsync(
                staged + SyncPaths.Mp3Extension,
                input,
                track.Source.Size,
                cancellationToken,
                (bytes, token) => this.SendProgressAsync(bytes, track.Source.Size, token));
            await VerifyHashAsync(
                afc,
                staged + SyncPaths.Mp3Extension,
                track.Source,
                cancellationToken);
            await afc.RenameNewFileAsync(staged + SyncPaths.Mp3Extension, staged, cancellationToken);
            await this.SendAsync(AtcCommands.FileComplete, Plist.Dict(
                (AtcKeys.AssetId, track.AssetId),
                (AtcKeys.Dataclass, AtcKeys.Media),
                (AtcKeys.AssetPath, track.DevicePath)), cancellationToken);
        }

        foreach (var asset in manifest.Where(asset => asset.Track is null))
        {
            await this.SendAsync(AtcCommands.FileError, Plist.Dict(
                (AtcKeys.AssetId, asset.AssetId),
                (AtcKeys.Dataclass, AtcKeys.Media),
                (AtcKeys.ErrorCode, SyncProtocol.AssetUnavailable),
                (AtcKeys.ErrorIsVisible, false)), cancellationToken);
        }

        await this.WaitAsync([AtcCommands.SyncFinished], cancellationToken);
    }

    internal static async Task VerifyHashAsync(AfcClient afc, string path, SourceTrack source, CancellationToken cancellationToken)
    {
        var actual = await afc.HashFileAsync(path, source.Size, cancellationToken);
        if (!StringComparer.Ordinal.Equals(actual, source.Sha256))
            throw new IOException($"iPhone audio read-back differs: {source.RelativePath}");
    }

    private Task SendProgressAsync(long bytes, long size, CancellationToken cancellationToken)
    {
        return this.SendAsync(AtcCommands.FileProgress, Plist.Dict(
            (AtcKeys.AssetProgress, size == 0 ? 1.0 : (double)bytes / size),
            (AtcKeys.OverallProgress, 0.0)), cancellationToken);
    }

    private Task SendAsync(string command, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        return client.Connection.SendAsync(Plist.Dict(
            (AtcKeys.Command, command), (AtcKeys.Session, SyncProtocol.Session), (AtcKeys.Parameters, parameters)), cancellationToken);
    }

    private async Task<Dictionary<string, object>> WaitAsync(string[] expected, CancellationToken cancellationToken)
    {
        for (var i = 0; i < SyncProtocol.MessageLimit; i++)
        {
            var message = await client.Connection.ReceiveAsync(cancellationToken);
            var command = message.Text(AtcKeys.Command);
            if (command == AtcCommands.Ping)
            {
                await client.Connection.SendAsync(Plist.Dict((AtcKeys.Command, AtcCommands.Pong), (AtcKeys.Session, message.Number(AtcKeys.Session))), cancellationToken);
                continue;
            }

            if (command is AtcCommands.ConnectionInvalid or AtcCommands.IdleExit)
                throw new IOException("The iPhone ended the music sync connection.");

            if (message.Number(AtcKeys.Session) != SyncProtocol.Session)
                continue;
            if (command is AtcCommands.SyncFailed or AtcCommands.FileError)
                throw new IOException($"The iPhone reported {command} during music synchronization.");

            if (command == AtcCommands.SyncAllowed)
            {
                if (!AtcClient.ReadSyncPermissions(message.Dictionary(AtcKeys.Parameters)).Allowed)
                    throw new IOException("The iPhone revoked music sync permission.");

                continue;
            }

            if (expected.Contains(command, StringComparer.Ordinal))
                return message;
            if (command is not (AtcCommands.Pong or AtcCommands.Progress or AtcCommands.AssetMetrics or AtcCommands.InstalledAssets or AtcCommands.Capabilities))
                throw new InvalidDataException($"Unexpected ATC sync command: {command}");
        }

        throw new IOException("ATC sync message limit exceeded.");
    }

    private static Dictionary<string, object> Anchors(uint anchor)
    {
        return Plist.Dict((AtcKeys.Media, anchor.ToString(CultureInfo.InvariantCulture)), (AtcKeys.VoiceMemo, SyncProtocol.VoiceMemoAnchor));
    }
}
