using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class SyncRecoveryRunner(
    SyncDeviceContext context,
    SyncJournal journal,
    SyncBatchExecutor executor)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pending = journal.ReadPending();
        var additions = pending.Additions.Select(track => track.Restore(journal.Assets(pending))).ToArray();
        DeviceCatalogue current;
        IReadOnlyList<DeviceTrack> remove;
        IReadOnlyList<PreparedTrack> retryArtwork;
        await context.Error.WriteLineAsync("Reconciling the interrupted sync before planning new changes...");
        await using (var phone = await context.OpenAsync(cancellationToken))
        {
            await using var syncLock = await phone.Afc.AcquireSyncLockAsync(cancellationToken);
            if (await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken) != pending.Identity)
            {
                throw new IOException("The pending operation belongs to a different library identity.");
            }

            current = await context.ReadCatalogueAsync(phone.Afc, cancellationToken);
            (remove, retryArtwork) = await this.AssessTransfersAsync(
                phone.Afc,
                pending,
                current,
                additions,
                cancellationToken);

            await ClearMetadataAsync(phone.Afc, pending, cancellationToken);
            if (additions.Length != 0)
            {
                var session = new AtcSyncSession(phone.Atc, context.Error);
                using var signer = await session.BeginAsync(pending.Identity, current.Anchor, cancellationToken);
            }

            SyncEngine.RequireUnchanged(current, await context.ReadCatalogueAsync(phone.Afc, cancellationToken));
            await this.ClearAssetsAsync(phone.Afc, additions, cancellationToken);
            if (remove.Count == 0 && retryArtwork.Count == 0)
            {
                this.SettleRecoveryJournal(pending, current, additions);
                await context.Error.WriteLineAsync("Interrupted operation reconciled; no operation was replayed.");
                return;
            }
        }

        // Remove only this unverified batch's unfilled identities through ATC. A new source plan retries them.
        var batch = CreateRecoveryBatch(current, remove, retryArtwork);
        await executor.ExecuteAsync(
            pending.Identity,
            current,
            batch,
            cancellationToken,
            replacePending: true);

        if (remove.Count != 0)
        {
            await context.Error.WriteLineAsync(
                $"Reconciled {DisplayFormat.Number(remove.Count)} unfinished insertions; verified tracks were retained.");
        }
    }

    private async Task<(IReadOnlyList<DeviceTrack> Remove, IReadOnlyList<PreparedTrack> RetryArtwork)> AssessTransfersAsync(
        AfcClient afc,
        PendingSync pending,
        DeviceCatalogue current,
        IReadOnlyList<PreparedTrack> additions,
        CancellationToken cancellationToken)
    {
        var remove = SyncRecovery.Assess(pending, current, additions).ToList();
        var retryArtwork = new List<PreparedTrack>();
        if (current.Anchor == pending.Before.Anchor) return (remove, retryArtwork);

        var candidates = additions.Where(track => remove.All(item => item.Id != track.Id)).ToArray();
        var progress = new OperationProgress(context.Error, "Verifying interrupted transfers", candidates.Length);
        var completed = 0;
        foreach (var track in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(completed++);
            var (removal, retry) = await VerifyTransferAsync(
                afc,
                current,
                track,
                cancellationToken);

            if (removal is not null) remove.Add(removal);

            if (retry is not null) retryArtwork.Add(retry);
        }

        progress.Report(completed);
        return (remove, retryArtwork);
    }

    private static async Task<(DeviceTrack? Remove, PreparedTrack? RetryArtwork)> VerifyTransferAsync(
        AfcClient afc,
        DeviceCatalogue catalogue,
        PreparedTrack track,
        CancellationToken cancellationToken)
    {
        var actual = catalogue.Tracks.SingleOrDefault(item => item.Id == track.Id);
        var audio = await afc.GetFileInfoAsync(track.DevicePath, cancellationToken);
        if (actual is null)
        {
            if (audio is not null)
            {
                throw new IOException("A recovery identity has orphaned permanent audio; retain the journal.");
            }

            return (null, null);
        }

        if (audio is null)
        {
            if (track.IsArtworkOnly)
            {
                throw new IOException("An existing artwork target lost its audio; recovery stopped.");
            }

            return (actual, null);
        }

        await AtcSyncSession.VerifyHashAsync(
            afc,
            track.DevicePath,
            track.Source,
            cancellationToken);

        if (track.Metadata.Artwork is not null
            && (!catalogue.TrackArtwork.TryGetValue(track.Id, out var artwork) || artwork.Path is null))
        {
            return track.IsArtworkOnly ? (null, track) : (actual, null);
        }

        await SyncEngine.VerifyArtworkAsync(
            afc,
            catalogue,
            track,
            cancellationToken);

        return (null, null);
    }

    private static SyncBatch CreateRecoveryBatch(
        DeviceCatalogue catalogue,
        IReadOnlyList<DeviceTrack> remove,
        IReadOnlyList<PreparedTrack> retryArtwork)
    {
        if (remove.Count != 0) return SyncBatch.Create(catalogue, [], remove);

        var id = catalogue.ArtworkHighWaterMark;
        var retry = retryArtwork.Select(track => track with
        {
            Metadata = track.Metadata with
            {
                Artwork = track.Metadata.Artwork! with { CacheId = ArtworkImage.Allocate(ref id) }
            }
        }).ToArray();

        return SyncBatch.Create(catalogue, retry, []);
    }

    private static async Task ClearMetadataAsync(AfcClient afc, PendingSync pending, CancellationToken cancellationToken)
    {
        await DeviceStaging.RemoveMetadataAsync(
            afc,
            pending.Operation,
            pending.Signature,
            cancellationToken);

        await SyncEngine.RequireEmptyStagingAsync(afc, cancellationToken);
    }

    private async Task ClearAssetsAsync(
        AfcClient afc,
        IReadOnlyList<PreparedTrack> additions,
        CancellationToken cancellationToken)
    {
        var cleanupProgress = new OperationProgress(context.Error, "Reconciling staged transfers", additions.Count);
        foreach (var track in additions)
        {
            await DeviceStaging.RemoveMatchingAsync(
                afc,
                SyncPaths.Airlock + track.AssetId + SyncPaths.Mp3Extension,
                track.LocalPath,
                cancellationToken);

            await DeviceStaging.RemoveMatchingAsync(
                afc,
                SyncPaths.Airlock + track.AssetId,
                track.LocalPath,
                cancellationToken);

            if (track.Metadata.Artwork is { } art)
            {
                await DeviceStaging.RemoveMatchingAsync(
                    afc,
                    SyncPaths.ArtworkAirlock + track.AssetId,
                    art.Bytes,
                    cancellationToken);
            }

            cleanupProgress.Advance();
        }
    }

    private void SettleRecoveryJournal(
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
}
