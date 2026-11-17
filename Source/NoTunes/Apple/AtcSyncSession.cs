using System.Globalization;
using System.Runtime.ExceptionServices;
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
        client.StartKeepAlive(cancellationToken);
        var ready = await this.WaitAsync([AtcCommands.ReadyForSync], cancellationToken);
        var parameters = ready.Dictionary(AtcKeys.Parameters);
        var anchors = parameters.Dictionary(AtcKeys.Anchors);
        if (!StringComparer.Ordinal.Equals(anchors.Text(AtcKeys.Media), anchor.ToString(CultureInfo.InvariantCulture)))
            throw new IOException("The live Media anchor changed. Recompute the sync plan.");

        if (anchors.TryGetValue(AtcKeys.VoiceMemo, out var voiceMemo)
            && !StringComparer.Ordinal.Equals(voiceMemo, SyncProtocol.VoiceMemoAnchor))
        {
            throw new IOException("Unsupported live VoiceMemo anchor; refusing to overwrite non-Media state.");
        }

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
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var transferFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repliesRemaining = manifest.Count;
        ExceptionDispatchInfo? controlFailure = null;
        var receiver = MonitorAsync();
        var sender = SendAssetsAsync();
        try
        {
            var first = await Task.WhenAny(receiver, sender);
            await first;
            await Task.WhenAll(receiver, sender);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch
        {
            // Preserve the control error when it cancels an in-flight AFC request.
            Volatile.Read(ref controlFailure)?.Throw();
            throw;
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(receiver, sender);
            }
            catch
            {
                // Both tasks have stopped; retain the original failure.
            }
        }

        async Task MonitorAsync()
        {
            try
            {
                await this.WaitAsync([AtcCommands.SyncFinished], stop.Token, transferFinished.Task);
                if (Volatile.Read(ref repliesRemaining) != 0)
                    throw new IOException("Sync finished before every requested asset was completed.");
            }
            catch (Exception exception)
            {
                if (!stop.IsCancellationRequested)
                {
                    Volatile.Write(ref controlFailure, ExceptionDispatchInfo.Capture(exception));
                    stop.Cancel();
                }

                throw;
            }
        }

        async Task SendAssetsAsync()
        {
            try
            {
                await this.TransferAssetsAsync(
                    afc,
                    manifest,
                    () => Interlocked.Decrement(ref repliesRemaining),
                    stop.Token);
            }
            finally
            {
                transferFinished.TrySetResult();
            }
        }
    }

    private async Task TransferAssetsAsync(
        AfcClient afc,
        IReadOnlyList<SyncAsset> manifest,
        Action completingAsset,
        CancellationToken cancellationToken)
    {
        foreach (var asset in manifest.Where(asset => asset.Track is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = asset.Track!;
            if (track.Metadata.Artwork is { } artwork)
            {
                await afc.EnsureDirectoryAsync(SyncPaths.ArtworkAirlock.TrimEnd('/'), cancellationToken);
                await DeviceStaging.WriteVerifiedAsync(
                    afc,
                    SyncPaths.ArtworkAirlock + track.AssetId,
                    artwork.Bytes,
                    cancellationToken);
            }

            if (track.IsArtworkOnly)
            {
                completingAsset();
                await this.SendAsync(AtcCommands.FileComplete, Plist.Dict(
                    (AtcKeys.AssetId, track.AssetId),
                    (AtcKeys.Dataclass, AtcKeys.Media),
                    (AtcKeys.AssetPath, string.Empty)), cancellationToken);
                continue;
            }

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
            completingAsset();
            await this.SendAsync(AtcCommands.FileComplete, Plist.Dict(
                (AtcKeys.AssetId, track.AssetId),
                (AtcKeys.Dataclass, AtcKeys.Media),
                (AtcKeys.AssetPath, track.DevicePath)), cancellationToken);
        }

        foreach (var asset in manifest.Where(asset => asset.Track is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            completingAsset();
            await this.SendAsync(AtcCommands.FileError, Plist.Dict(
                (AtcKeys.AssetId, asset.AssetId),
                (AtcKeys.Dataclass, AtcKeys.Media),
                (AtcKeys.ErrorCode, SyncProtocol.AssetUnavailable),
                (AtcKeys.ErrorIsVisible, false)), cancellationToken);
        }

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

    private async Task<Dictionary<string, object>> WaitAsync(
        string[] expected,
        CancellationToken cancellationToken,
        Task? transferFinished = null)
    {
        for (var i = 0; i < SyncProtocol.MessageLimit; i++)
        {
            var message = transferFinished is null
                ? await client.Connection.ReceiveAsync(cancellationToken)
                : await this.ReceiveDuringTransferAsync(transferFinished, cancellationToken);
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

    private async Task<Dictionary<string, object>> ReceiveDuringTransferAsync(
        Task transferFinished,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = client.Connection.ReceiveAsync(deadline.Token, useTimeout: false);
        await Task.WhenAny(receive, transferFinished);
        // Uploads may be silent on ATC; once they end, each response has the usual request timeout.
        if (transferFinished.IsCompleted) deadline.CancelAfter(client.RequestTimeout);

        try
        {
            return await receive;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out waiting for the iPhone to finish music synchronization.", exception);
        }
    }

    private static Dictionary<string, object> Anchors(uint anchor)
    {
        return Plist.Dict((AtcKeys.Media, anchor.ToString(CultureInfo.InvariantCulture)), (AtcKeys.VoiceMemo, SyncProtocol.VoiceMemoAnchor));
    }
}
