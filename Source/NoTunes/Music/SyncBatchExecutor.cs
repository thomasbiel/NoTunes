using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class SyncBatchExecutor(SyncDeviceContext context, SyncJournal journal)
{
    public async Task<int> ExecutePlanAsync(
        LibraryIdentity identity,
        DeviceCatalogue catalogue,
        PreparedSync prepared,
        CancellationToken cancellationToken)
    {
        var batches = 0;
        // Verify additions before removing tracks; pending removals never fund the free-space estimate.
        await ExecuteSequenceAsync(prepared.Additions, (current, tracks) => SyncBatch.Create(current, tracks, []));
        await ExecuteSequenceAsync(prepared.Plan.Delete, (current, tracks) => SyncBatch.Create(current, [], tracks));
        await ExecuteSequenceAsync(prepared.Artwork, (current, tracks) => SyncBatch.Create(current, tracks, []));
        return batches;

        async Task ExecuteSequenceAsync<T>(
            IReadOnlyList<T> tracks,
            Func<DeviceCatalogue, IReadOnlyList<T>, SyncBatch> createBatch)
        {
            var position = 0;
            while (position < tracks.Count)
            {
                var changes = tracks.Skip(position).Take(SyncProtocol.MaxTrackChanges).ToArray();
                var batch = createBatch(catalogue, changes);
                catalogue = await this.ExecuteAsync(
                    identity,
                    catalogue,
                    batch,
                    cancellationToken);

                position += batch.Additions.Count + batch.Deletions.Count;
                batches++;
            }
        }
    }

    public async Task<DeviceCatalogue> ExecuteAsync(
        LibraryIdentity identity,
        DeviceCatalogue expected,
        SyncBatch batch,
        CancellationToken cancellationToken,
        bool replacePending = false)
    {
        var additions = batch.Additions;
        var deletions = batch.Deletions;
        await using var phone = await context.OpenAsync(cancellationToken);

        await using var syncLock = await phone.Afc.AcquireSyncLockAsync(cancellationToken);
        var currentIdentity = await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken);
        var before = await context.ReadCatalogueAsync(phone.Afc, cancellationToken);
        SyncEngine.RequireUnchanged(expected, before);
        if (currentIdentity != identity) throw new IOException("The iPhone library identity changed after planning.");

        await SyncDeviceContext.ValidateSavedAnchorsAsync(phone.Afc, before, cancellationToken);
        var transferBytes = additions.Sum(track => (track.IsArtworkOnly ? 0 : track.Source.Size)
            + (track.Metadata.Artwork?.Length ?? 0));

        await SyncDeviceContext.RequireSpaceAsync(phone.Afc, transferBytes, cancellationToken);

        await SyncEngine.RequireEmptyStagingAsync(phone.Afc, cancellationToken);
        var session = new AtcSyncSession(phone.Atc, context.Error, context.Debug);
        using var signer = await session.BeginAsync(identity, before.Anchor, cancellationToken);
        await phone.Afc.EnsureDirectoryAsync(SyncPaths.MetadataDirectory, cancellationToken);
        await SyncEngine.RequireEmptyStagingAsync(phone.Afc, cancellationToken);
        var operation = batch.Operation;
        var signature = signer.Sign(operation);

        await journal.StartBatchAsync(
            identity,
            before,
            batch,
            signature,
            cancellationToken,
            replacePending);

        if (replacePending)
        {
            var pending = journal.ReadPending();
            additions = [.. pending.Additions.Select(track => track.Restore(journal.Assets(pending)))];
        }

        await DeviceStaging.WriteMetadataAsync(
            phone.Afc,
            operation,
            signature,
            cancellationToken);

        var deletionProgress = new OperationProgress(context.Error, "Removing obsolete iPhone tracks", deletions.Count);
        await session.FinishMetadataAsync(before.Anchor, cancellationToken);
        var retained = before.Tracks.Select(track => track.Id).Except(deletions.Select(track => track.Id)).ToHashSet();
        await session.TransferAsync(
            phone.Afc,
            additions,
            retained,
            cancellationToken);

        var after = await context.ReadCatalogueAsync(phone.Afc, cancellationToken);
        SyncEngine.VerifyResult(
            before,
            after,
            additions,
            deletions);

        await this.VerifyTransfersAsync(
            phone.Afc,
            after,
            additions,
            cancellationToken);

        if (await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken) != identity)
        {
            throw new IOException("Library identity changed during synchronization.");
        }

        journal.Complete();
        deletionProgress.Report(deletions.Count);
        await context.Error.WriteLineAsync($"Verified Media revision {after.Anchor}: "
            + $"{additions.Count} added, {deletions.Count} removed.");

        return after;
    }

    private async Task VerifyTransfersAsync(
        AfcClient afc,
        DeviceCatalogue catalogue,
        IReadOnlyList<PreparedTrack> additions,
        CancellationToken cancellationToken)
    {
        var verificationProgress = new OperationProgress(context.Error, "Verifying transferred tracks", additions.Count);
        foreach (var track in additions)
        {
            await AtcSyncSession.VerifyHashAsync(
                afc,
                track.DevicePath,
                track.Source,
                cancellationToken);

            await SyncEngine.VerifyArtworkAsync(
                afc,
                catalogue,
                track,
                cancellationToken);

            verificationProgress.Advance();
        }
    }

}
