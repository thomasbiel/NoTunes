using System.Globalization;
using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Platform;

namespace NoTunes.Music;

public sealed class SyncEngine(SourceScanner scanner, ISourceFileSystem fileSystem, IUsbMuxTransport transport, TextWriter error)
{
    public async Task<SyncResult> RunAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        await error.WriteLineAsync("Scanning and hashing source music...");
        var source = await scanner.ScanAsync(SourceSettings.ResolveFolder(options.Folder), cancellationToken);
        var timeout = TimeSpan.FromSeconds(options.Timeout);
        var mux = new UsbMuxClient(transport, timeout);
        var device = Commands.SelectDevice(await mux.ListUsbDevicesAsync(cancellationToken), options.Device);
        SyncPlan plan;
        DeviceCatalogue catalogue;
        LibraryIdentity identity;

        await using (var phone = await DeviceSyncConnection.OpenAsync(
            mux,
            device,
            timeout,
            cancellationToken))
        {
            identity = await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken);
            catalogue = await ReadCatalogueAsync(phone.Afc, cancellationToken);
            await ValidateSavedAnchorsAsync(phone.Afc, catalogue, cancellationToken);
            await error.WriteLineAsync($"Comparing {source.Tracks.Count} source tracks with {catalogue.Tracks.Count} local iPhone tracks...");
            plan = await SyncPlanner.CreateAsync(
                device.Udid,
                source,
                catalogue,
                (track, token) => this.ReadHashAsync(phone.Afc, track, token),
                cancellationToken);

            if (!options.DryRun && plan.HasChanges)
            {
                RequireCompleteAudio(catalogue);
                if (source.Tracks.Count == 0 && !options.AllowEmptySource)
                    throw new IOException("The source contains no MP3 files. Use --allow-empty-source to deliberately remove all synced music.");

                await RequireSpaceAsync(phone.Afc, plan.TransferBytes, cancellationToken);
            }
        }

        if (options.DryRun || !plan.HasChanges) return new SyncResult(plan, options.DryRun, 0);

        await error.WriteLineAsync($"Plan: add {plan.Add.Count}, delete {plan.Delete.Count}, unchanged {plan.Unchanged}.");
        var stateRoot = options.StateFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppleClient.Name);
        using var journal = new SyncJournal(stateRoot, device.Udid);
        using var workspace = new SyncWorkspace();
        await error.WriteLineAsync("Preparing verified local copies and MP3 metadata before changing the iPhone...");
        var prepared = await SourcePreparation.PrepareAsync(
            source,
            plan,
            catalogue,
            fileSystem,
            workspace,
            cancellationToken);

        var batches = 0;
        // Finish and verify additions first; removals are separate batches and never fund the required free-space estimate.
        var added = 0;
        while (added < prepared.Count)
        {
            var batch = SyncBatch.Create(catalogue, prepared.Skip(added).Take(SyncProtocol.MaxTrackChanges).ToArray(), []);
            catalogue = await this.ExecuteBatchAsync(
                mux,
                device,
                timeout,
                identity,
                catalogue,
                batch,
                journal,
                cancellationToken);

            added += batch.Additions.Count;
            batches++;
        }

        var deleted = 0;
        while (deleted < plan.Delete.Count)
        {
            var batch = SyncBatch.Create(catalogue, [], plan.Delete.Skip(deleted).Take(SyncProtocol.MaxTrackChanges).ToArray());
            catalogue = await this.ExecuteBatchAsync(
                mux,
                device,
                timeout,
                identity,
                catalogue,
                batch,
                journal,
                cancellationToken);

            deleted += batch.Deletions.Count;
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
        CancellationToken cancellationToken)
    {
        var additions = batch.Additions;
        var deletions = batch.Deletions;
        await using var phone = await DeviceSyncConnection.OpenAsync(
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
        await RequireSpaceAsync(phone.Afc, additions.Sum(track => track.Source.Size), cancellationToken);
        await RequireEmptyStagingAsync(phone.Afc, cancellationToken);
        var session = new AtcSyncSession(phone.Atc, error);
        using var signer = await session.BeginAsync(identity, before.Anchor, cancellationToken);
        await phone.Afc.EnsureDirectoryAsync(SyncPaths.MetadataDirectory, cancellationToken);
        await RequireEmptyStagingAsync(phone.Afc, cancellationToken);
        var operation = batch.Operation;
        var signature = signer.Sign(operation);

        var batchPlan = new SyncPlan(
            device.Udid,
            before.Anchor,
            [.. additions.Select(track => track.Source)],
            deletions,
            before.Tracks.Count - deletions.Count);

        journal.Start(
            identity,
            batchPlan,
            operation,
            signature);

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
        }

        if (await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken) != identity)
        {
            throw new IOException("Library identity changed during synchronization.");
        }

        journal.Complete();
        await error.WriteLineAsync($"Verified Media revision {after.Anchor}: {additions.Count} added, {deletions.Count} removed.");
        return after;
    }

    internal static void VerifyResult(
        DeviceCatalogue before,
        DeviceCatalogue after,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlyList<DeviceTrack> deletions)
    {
        if (after.DatabaseId != before.DatabaseId || after.ContainerId != before.ContainerId
            || after.Anchor != checked(before.Anchor + 1))
            throw new IOException("The iPhone did not commit the expected library revision.");

        var deletedIds = deletions.Select(track => track.Id).ToHashSet();
        var expectedIds = before.Tracks
            .Select(track => track.Id)
            .Where(id => !deletedIds.Contains(id))
            .Concat(additions.Select(track => track.Id))
            .ToHashSet();

        if (!expectedIds.SetEquals(after.Tracks.Select(track => track.Id)))
        {
            throw new IOException("The iPhone track inventory differs from the requested changes.");
        }

        var actual = after.Tracks.ToDictionary(track => track.Id);
        if (!after.SortNames.Preserves(before.SortNames, allowRemovedGroups: true)
            || before.Tracks.Where(track => !deletedIds.Contains(track.Id)).Any(track => actual[track.Id] != track)
            || additions.Any(track => actual[track.Id].Path != track.DevicePath || actual[track.Id].Size != track.Source.Size
                || actual[track.Id].Title != (string)track.Metadata.Item[MediaKeys.Title]))
        {
            throw new IOException("The iPhone track metadata differs after synchronization.");
        }
    }

    private static void RequireUnchanged(DeviceCatalogue expected, DeviceCatalogue actual)
    {
        if (expected.DatabaseId != actual.DatabaseId || expected.ContainerId != actual.ContainerId
            || expected.Anchor != actual.Anchor || !expected.Tracks.SequenceEqual(actual.Tracks)
            || !expected.OccupiedIds.SetEquals(actual.OccupiedIds)
            || !actual.SortNames.Preserves(expected.SortNames) || !expected.SortNames.Preserves(actual.SortNames))
            throw new IOException("The iPhone catalogue changed after planning. Run the sync again.");
    }

    private async Task<string?> ReadHashAsync(AfcClient afc, DeviceTrack track, CancellationToken cancellationToken)
    {
        if (track.Path is not { } path) return null;

        var info = await afc.GetFileInfoAsync(path, cancellationToken);
        if (info is null) return null;

        if (!long.TryParse(
            info.GetValueOrDefault(AfcKeys.FileSize),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var size)
            || size != track.Size)
        {
            throw new IOException($"The catalogue file size disagrees with the iPhone file: {track.Title}");
        }

        await error.WriteLineAsync($"Compare: {track.Title}");
        return await afc.HashFileAsync(path, track.Size, cancellationToken);
    }

    internal static void RequireCompleteAudio(DeviceCatalogue catalogue)
    {
        if (catalogue.Tracks.Any(track => track.IsIncomplete))
            throw new IOException("The iPhone contains unfinished music transfers. Use --dry-run to inspect them "
                + "and reconcile the interrupted sync before starting another write session.");
    }

    private static async Task RequireEmptyStagingAsync(AfcClient afc, CancellationToken cancellationToken)
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
        if (!long.TryParse(
            storage.GetValueOrDefault(AfcKeys.FreeBytes),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var free)
            || free < checked(bytes + SyncProtocol.MetadataReserveBytes))
            throw new IOException("Insufficient verified iPhone free space for additions and the metadata reserve.");
    }

    private static async Task WriteVerifiedAsync(AfcClient afc, string path, byte[] content, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(content, writable: false);
        await afc.WriteNewFileAsync(
            path,
            input,
            content.Length,
            cancellationToken);

        var actual = await afc.ReadFileAsync(path, content.Length, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(content)) throw new IOException("Staged sync metadata failed read-back verification.");
    }

    private static async Task ValidateSavedAnchorsAsync(AfcClient afc, DeviceCatalogue catalogue, CancellationToken cancellationToken)
    {
        const string path = SyncPaths.SavedCatalogue;
        if (await afc.GetFileInfoAsync(path, cancellationToken) is not null)
            SavedHostAnchors.Validate(await afc.ReadFileAsync(path, SyncProtocol.MaxCatalogueBytes, cancellationToken), catalogue.DatabaseId);
    }

    private static async Task<DeviceCatalogue> ReadCatalogueAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        return await CatalogueReader.ReadAsync(await CatalogueSnapshot.CaptureAsync(afc, cancellationToken), cancellationToken);
    }
}
