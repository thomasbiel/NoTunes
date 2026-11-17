using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Platform;

namespace NoTunes.Music;

public sealed class SyncEngine(
    SourceScanner scanner,
    ISourceFileSystem fileSystem,
    IUsbMuxTransport transport,
    TextWriter error)
{
    internal SyncConnectionFactory OpenConnection { get; init; } = OpenDeviceConnectionAsync;

    private static async Task<IDeviceSyncConnection> OpenDeviceConnectionAsync(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await DeviceSyncConnection.OpenAsync(mux, device, timeout, cancellationToken);
    }

    public async Task<SyncResult> RunAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        await error.WriteLineAsync("Scanning and hashing source music...");
        var source = await scanner.ScanAsync(SourceSettings.ResolveFolder(options.Folder), cancellationToken);
        var timeout = TimeSpan.FromSeconds(options.Timeout);
        var mux = new UsbMuxClient(transport, timeout);
        var device = DeviceOptions.SelectDevice(await mux.ListUsbDevicesAsync(cancellationToken), options.Device);
        using var journal = options.DryRun
            ? null
            : new SyncJournal(DeviceState.Root(options.StateFolder), device.Udid, allowPending: true);

        if (journal is { HasPending: true })
        {
            await this.RecoverAsync(mux, device, timeout, journal, cancellationToken);
        }

        SyncPlan plan;
        DeviceCatalogue catalogue;
        LibraryIdentity identity;

        await using (var phone = await this.OpenConnection(
            mux,
            device,
            timeout,
            cancellationToken))
        {
            identity = await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken);
            catalogue = await ReadCatalogueAsync(phone.Afc, cancellationToken);
            await ValidateSavedAnchorsAsync(phone.Afc, catalogue, cancellationToken);
            await error.WriteLineAsync($"Comparing {source.Tracks.Count} source tracks "
                + $"with {catalogue.Tracks.Count} local iPhone tracks...");
            plan = await SyncPlanner.CreateAsync(
                device.Udid,
                source,
                catalogue,
                (track, token) => this.ReadHashAsync(phone.Afc, track, token),
                cancellationToken);

            if (!options.DryRun && plan.HasChanges)
            {
                ValidateWritePlan(
                    source,
                    plan,
                    catalogue,
                    options.AllowEmptySource);

                await RequireSpaceAsync(phone.Afc, plan.TransferBytes, cancellationToken);
            }
        }

        using var workspace = new SyncWorkspace();
        await error.WriteLineAsync("Preparing verified local copies and MP3 metadata before changing the iPhone...");
        var prepared = await SourcePreparation.PrepareAsync(
            source,
            plan,
            catalogue,
            fileSystem,
            workspace,
            cancellationToken);

        var missingArtwork = plan.Matches.Where(match => !HasRegisteredArtwork(catalogue, match.Device.Id)).ToArray();

        var lastArtworkId = prepared
            .Where(track => track.Metadata.Artwork is not null)
            .Select(track => track.Metadata.Artwork!.CacheId)
            .DefaultIfEmpty(catalogue.ArtworkHighWaterMark)
            .Max();

        var repairs = await SourcePreparation.PrepareAsync(
            source,
            plan with { Add = [.. missingArtwork.Select(match => match.Source)] },
            catalogue with { ArtworkHighWaterMark = lastArtworkId },
            fileSystem,
            workspace,
            cancellationToken,
            artworkOnly: true);

        var matches = missingArtwork.ToDictionary(match => match.Source.RelativePath, StringComparer.Ordinal);
        var artworkUpdates = repairs
            .Select(track => track with
            {
                Id = matches[track.Source.RelativePath].Device.Id,
                IsArtworkOnly = true,
                ExistingDevicePath = matches[track.Source.RelativePath].Device.Path
            })
            .ToArray();

        plan = plan with { Artwork = [.. artworkUpdates.Select(track => track.Source)] };
        if (options.DryRun || !plan.HasChanges) return new SyncResult(plan, options.DryRun, 0);

        await error.WriteLineAsync($"Plan: add {plan.Add.Count}, delete {plan.Delete.Count}, "
            + $"repair {artworkUpdates.Length} covers, unchanged {plan.Unchanged}.");

        var batches = 0;
        // Verify additions before removing tracks; pending removals never fund the free-space estimate.
        var added = 0;
        while (added < prepared.Count)
        {
            var batch = SyncBatch.Create(catalogue, [.. prepared.Skip(added).Take(SyncProtocol.MaxTrackChanges)], []);
            catalogue = await this.ExecuteBatchAsync(
                mux,
                device,
                timeout,
                identity,
                catalogue,
                batch,
                journal!,
                cancellationToken);

            added += batch.Additions.Count;
            batches++;
        }

        var deleted = 0;
        while (deleted < plan.Delete.Count)
        {
            var deletions = plan.Delete.Skip(deleted).Take(SyncProtocol.MaxTrackChanges).ToArray();
            var batch = SyncBatch.Create(catalogue, [], deletions);
            catalogue = await this.ExecuteBatchAsync(
                mux,
                device,
                timeout,
                identity,
                catalogue,
                batch,
                journal!,
                cancellationToken);

            deleted += batch.Deletions.Count;
            batches++;
        }

        var repaired = 0;
        while (repaired < artworkUpdates.Length)
        {
            var updates = artworkUpdates.Skip(repaired).Take(SyncProtocol.MaxTrackChanges).ToArray();
            var batch = SyncBatch.Create(catalogue, updates, []);
            catalogue = await this.ExecuteBatchAsync(
                mux,
                device,
                timeout,
                identity,
                catalogue,
                batch,
                journal!,
                cancellationToken);

            repaired += batch.Additions.Count;
            batches++;
        }

        return new SyncResult(plan, false, batches);
    }

    private async Task<DeviceCatalogue> ExecuteBatchAsync(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        LibraryIdentity identity,
        DeviceCatalogue expected,
        SyncBatch batch,
        SyncJournal journal,
        CancellationToken cancellationToken,
        bool replacePending = false)
    {
        var additions = batch.Additions;
        var deletions = batch.Deletions;
        await using var phone = await this.OpenConnection(
            mux,
            device,
            timeout,
            cancellationToken);

        await using var syncLock = await phone.Afc.AcquireSyncLockAsync(cancellationToken);
        var currentIdentity = await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken);
        var before = await ReadCatalogueAsync(phone.Afc, cancellationToken);
        RequireUnchanged(expected, before);
        if (currentIdentity != identity) throw new IOException("The iPhone library identity changed after planning.");

        await ValidateSavedAnchorsAsync(phone.Afc, before, cancellationToken);
        var transferBytes = additions.Sum(track => (track.IsArtworkOnly ? 0 : track.Source.Size)
            + (track.Metadata.Artwork?.Bytes.Length ?? 0));
        await RequireSpaceAsync(phone.Afc, transferBytes, cancellationToken);

        await RequireEmptyStagingAsync(phone.Afc, cancellationToken);
        var session = new AtcSyncSession(phone.Atc, error);
        using var signer = await session.BeginAsync(identity, before.Anchor, cancellationToken);
        await phone.Afc.EnsureDirectoryAsync(SyncPaths.MetadataDirectory, cancellationToken);
        await RequireEmptyStagingAsync(phone.Afc, cancellationToken);
        var operation = batch.Operation;
        var signature = signer.Sign(operation);

        await journal.StartBatchAsync(
            identity,
            before,
            batch,
            signature,
            cancellationToken,
            replacePending);

        await WriteVerifiedAsync(
            phone.Afc,
            SyncPaths.Metadata,
            operation,
            cancellationToken);

        await WriteVerifiedAsync(
            phone.Afc,
            SyncPaths.Metadata + SyncPaths.SignatureSuffix,
            signature,
            cancellationToken);

        await session.FinishMetadataAsync(before.Anchor, cancellationToken);
        var retained = before.Tracks.Select(track => track.Id).Except(deletions.Select(track => track.Id)).ToHashSet();
        await session.TransferAsync(
            phone.Afc,
            additions,
            retained,
            cancellationToken);

        var after = await ReadCatalogueAsync(phone.Afc, cancellationToken);
        VerifyResult(
            before,
            after,
            additions,
            deletions);

        foreach (var track in additions)
        {
            await AtcSyncSession.VerifyHashAsync(
                phone.Afc,
                track.DevicePath,
                track.Source,
                cancellationToken);

            await VerifyArtworkAsync(phone.Afc, after, track, cancellationToken);
        }

        if (await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken) != identity)
        {
            throw new IOException("Library identity changed during synchronization.");
        }

        journal.Complete();
        await error.WriteLineAsync($"Verified Media revision {after.Anchor}: "
            + $"{additions.Count} added, {deletions.Count} removed.");
        return after;
    }

    private async Task RecoverAsync(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        SyncJournal journal,
        CancellationToken cancellationToken)
    {
        var pending = journal.ReadPending();
        var additions = pending.Additions.Select(track => track.Restore(journal.Assets(pending))).ToArray();
        DeviceCatalogue current;
        var remove = new List<DeviceTrack>();
        var retryArtwork = new List<PreparedTrack>();
        await error.WriteLineAsync("Reconciling the interrupted sync before planning new changes...");
        await using (var phone = await this.OpenConnection(mux, device, timeout, cancellationToken))
        {
            await using var syncLock = await phone.Afc.AcquireSyncLockAsync(cancellationToken);
            if (await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken) != pending.Identity)
            {
                throw new IOException("The pending operation belongs to a different library identity.");
            }

            current = await ReadCatalogueAsync(phone.Afc, cancellationToken);
            remove.AddRange(SyncRecovery.Assess(pending, current, additions));
            if (current.Anchor != pending.Before.Anchor)
            {
                foreach (var track in additions.Where(track => remove.All(item => item.Id != track.Id)))
                {
                    var actual = current.Tracks.SingleOrDefault(item => item.Id == track.Id);
                    if (actual is null)
                    {
                        if (await phone.Afc.GetFileInfoAsync(track.DevicePath, cancellationToken) is not null)
                        {
                            throw new IOException(
                                "A recovery identity has orphaned permanent audio; retain the journal.");
                        }

                        continue;
                    }

                    if (await phone.Afc.GetFileInfoAsync(track.DevicePath, cancellationToken) is null)
                    {
                        if (track.IsArtworkOnly)
                        {
                            throw new IOException("An existing artwork target lost its audio; recovery stopped.");
                        }

                        remove.Add(actual);
                        continue;
                    }

                    await AtcSyncSession.VerifyHashAsync(phone.Afc, track.DevicePath, track.Source, cancellationToken);
                    if (track.Metadata.Artwork is not null && !HasRegisteredArtwork(current, track.Id))
                    {
                        if (track.IsArtworkOnly)
                        {
                            retryArtwork.Add(track);
                        }
                        else
                        {
                            remove.Add(actual);
                        }

                        continue;
                    }

                    await VerifyArtworkAsync(phone.Afc, current, track, cancellationToken);
                }
            }

            await DeviceStaging.RemoveMatchingAsync(
                phone.Afc,
                SyncPaths.Metadata,
                pending.Operation,
                cancellationToken);
            await DeviceStaging.RemoveMatchingAsync(
                phone.Afc,
                SyncPaths.Metadata + SyncPaths.SignatureSuffix,
                pending.Signature,
                cancellationToken);

            await RequireEmptyStagingAsync(phone.Afc, cancellationToken);
            var session = new AtcSyncSession(phone.Atc, error);
            using var signer = await session.BeginAsync(pending.Identity, current.Anchor, cancellationToken);
            RequireUnchanged(current, await ReadCatalogueAsync(phone.Afc, cancellationToken));
            foreach (var track in additions)
            {
                await DeviceStaging.RemoveMatchingAsync(
                    phone.Afc,
                    SyncPaths.Airlock + track.AssetId + SyncPaths.Mp3Extension,
                    track.LocalPath,
                    cancellationToken);

                await DeviceStaging.RemoveMatchingAsync(
                    phone.Afc,
                    SyncPaths.Airlock + track.AssetId,
                    track.LocalPath,
                    cancellationToken);

                if (track.Metadata.Artwork is { } art)
                {
                    await DeviceStaging.RemoveMatchingAsync(
                        phone.Afc,
                        SyncPaths.ArtworkAirlock + track.AssetId,
                        art.Bytes,
                        cancellationToken);
                }
            }

            if (remove.Count == 0 && retryArtwork.Count == 0)
            {
                SettleRecoveryJournal(
                    journal,
                    pending,
                    current,
                    additions);

                await error.WriteLineAsync("Interrupted operation reconciled; no operation was replayed.");
                return;
            }
        }

        if (remove.Count == 0)
        {
            var id = current.ArtworkHighWaterMark;
            var retry = retryArtwork.Select(track => track with
            {
                Metadata = track.Metadata with
                {
                    Artwork = track.Metadata.Artwork! with { CacheId = ArtworkImage.Allocate(ref id) }
                }
            }).ToArray();

            await this.ExecuteBatchAsync(
                mux,
                device,
                timeout,
                pending.Identity,
                current,
                SyncBatch.Create(current, retry, []),
                journal,
                cancellationToken,
                replacePending: true);

            return;
        }

        // Remove only this unverified batch's unfilled identities through ATC. A new source plan retries them.
        await this.ExecuteBatchAsync(
            mux,
            device,
            timeout,
            pending.Identity,
            current,
            SyncBatch.Create(current, [], remove),
            journal,
            cancellationToken,
            replacePending: true);

        await error.WriteLineAsync($"Reconciled {remove.Count} unfinished insertions; verified tracks were retained.");
    }

    internal static async Task VerifyArtworkAsync(
        AfcClient afc,
        DeviceCatalogue catalogue,
        PreparedTrack track,
        CancellationToken cancellationToken)
    {
        if (track.Metadata.Artwork is not { } expected) return;

        if (!catalogue.TrackArtwork.TryGetValue(track.Id, out var artwork)
            || artwork.CacheId != expected.CacheId || artwork.Path is null)
        {
            throw new IOException("The iPhone did not register the expected cover: " + track.Source.RelativePath);
        }

        var path = SyncPaths.ArtworkOriginals + artwork.Path;
        var hash = await afc.HashFileAsync(path, expected.Bytes.Length, cancellationToken);
        if (hash != expected.Sha256) throw new IOException("Permanent artwork differs from the prepared JPEG.");
    }

    internal static void VerifyResult(
        DeviceCatalogue before,
        DeviceCatalogue after,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlyList<DeviceTrack> deletions)
    {
        CatalogueVerification.VerifyResult(
            before,
            after,
            additions,
            deletions);
    }

    internal static void RequireUnchanged(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        CatalogueVerification.RequireUnchanged(expected, actual);
    }

    private static bool HasRegisteredArtwork(DeviceCatalogue catalogue, long id)
    {
        return catalogue.TrackArtwork.TryGetValue(id, out var artwork) && artwork.Path is not null;
    }

    private static void SettleRecoveryJournal(
        SyncJournal journal,
        PendingSync pending,
        DeviceCatalogue current,
        IReadOnlyList<PreparedTrack> additions)
    {
        if (current.Anchor == pending.Before.Anchor)
        {
            journal.ResolveUnapplied();
            return;
        }

        var currentIds = current.Tracks.Select(track => track.Id).ToHashSet();
        var remainingDeletions = pending.Deletions.Any(track => currentIds.Contains(track.Id));
        var missingAdditions = additions.Any(track => !currentIds.Contains(track.Id));
        if (remainingDeletions || missingAdditions)
        {
            journal.ResolveObserved();
        }
        else
        {
            journal.Complete();
        }
    }

    private async Task<string?> ReadHashAsync(AfcClient afc, DeviceTrack track, CancellationToken cancellationToken)
    {
        if (track.Path is not { } path) return null;

        var info = await afc.GetFileInfoAsync(path, cancellationToken);
        if (info is null) return null;

        var fileSize = info.GetValueOrDefault(AfcKeys.FileSize);
        if (!ProtocolNumbers.TryParseNonNegativeInt64(fileSize, out var size) || size != track.Size)
        {
            throw new IOException($"The catalogue file size disagrees with the iPhone file: {track.Title}");
        }

        await error.WriteLineAsync($"Compare: {track.Title}");
        return await afc.HashFileAsync(path, track.Size, cancellationToken);
    }

    internal static void ValidateWritePlan(
        SourceLibrary source,
        SyncPlan plan,
        DeviceCatalogue catalogue,
        bool allowEmptySource)
    {
        RequireCompleteAudio(catalogue);
        if (source.Tracks.Count == 0 && !allowEmptySource)
        {
            throw new IOException("The source contains no MP3 files. Use --allow-empty-source to deliberately remove all synced music.");
        }

        if ((long)catalogue.Tracks.Count + plan.Add.Count > CatalogueReader.MaximumTracks)
        {
            throw new IOException(
                $"The sync would exceed the {CatalogueReader.MaximumTracks}-track catalogue limit "
                + "while additions are verified before deletions. No changes were started.");
        }
    }

    internal static void RequireCompleteAudio(DeviceCatalogue catalogue)
    {
        if (catalogue.Tracks.Any(track => track.IsIncomplete))
        {
            throw new IOException(
                "The iPhone contains unfinished music transfers. Use --dry-run to inspect them "
                + "and reconcile the interrupted sync before starting another write session.");
        }
    }

    internal static async Task RequireEmptyStagingAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        if (await afc.GetFileInfoAsync(SyncPaths.MetadataDirectory, cancellationToken) is null) return;

        if ((await afc.ReadDirectoryAsync(SyncPaths.MetadataDirectory, cancellationToken)).Count != 0)
        {
            throw new IOException("The iPhone metadata staging directory is occupied. Resolve the earlier sync first.");
        }
    }

    private static async Task RequireSpaceAsync(AfcClient afc, long bytes, CancellationToken cancellationToken)
    {
        var storage = await afc.GetDeviceInfoAsync(cancellationToken);
        if (!ProtocolNumbers.TryParseNonNegativeInt64(storage.GetValueOrDefault(AfcKeys.FreeBytes), out var free)
            || free < checked(bytes + SyncProtocol.MetadataReserveBytes))
        {
            throw new IOException("Insufficient verified iPhone free space for additions and the metadata reserve.");
        }
    }

    private static async Task WriteVerifiedAsync(
        AfcClient afc,
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(content, writable: false);
        await afc.WriteNewFileAsync(
            path,
            input,
            content.Length,
            cancellationToken);

        var actual = await afc.ReadFileAsync(path, content.Length, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(content))
            throw new IOException("Staged sync metadata failed read-back verification.");
    }

    private static async Task ValidateSavedAnchorsAsync(
        AfcClient afc,
        DeviceCatalogue catalogue,
        CancellationToken cancellationToken)
    {
        const string path = SyncPaths.SavedCatalogue;
        if (await afc.GetFileInfoAsync(path, cancellationToken) is not null)
        {
            var bytes = await afc.ReadFileAsync(path, SyncProtocol.MaxCatalogueBytes, cancellationToken);
            SavedHostAnchors.Validate(bytes, catalogue.DatabaseId);
        }
    }

    private static async Task<DeviceCatalogue> ReadCatalogueAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        var snapshot = await CatalogueSnapshot.CaptureAsync(afc, cancellationToken);
        var catalogue = await CatalogueReader.ReadAsync(snapshot, cancellationToken);
        return catalogue with
        {
            ArtworkHighWaterMark = await ArtworkDatabase.HighestAsync(
                afc, catalogue.ArtworkHighWaterMark, cancellationToken)
        };
    }
}
