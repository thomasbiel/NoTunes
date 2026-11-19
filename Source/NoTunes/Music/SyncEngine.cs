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
    internal string? DeviceHashCacheDirectory { get; init; }

    internal SyncConnectionFactory OpenConnection { get; init; } = OpenDeviceConnectionAsync;

    internal GroupNamePrompt? PromptForGroupName { get; init; }

    private static async Task<IDeviceSyncConnection> OpenDeviceConnectionAsync(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await DeviceSyncConnection.OpenAsync(
            mux,
            device,
            timeout,
            cancellationToken);
    }

    public async Task<SyncResult> RunAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        await error.WriteLineAsync("Scanning and hashing source music...");
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var source = CancelOnFailureAsync(scanner.ScanInBackgroundAsync(
            SourceSettings.ResolveFolder(options.Folder), preparation.Token), preparation);

        var sync = CancelOnFailureAsync(this.RunWithDeviceAsync(options, source, preparation.Token), preparation);
        await Task.WhenAll(source, sync);
        return await sync;
    }

    private static async Task<T> CancelOnFailureAsync<T>(Task<T> operation, CancellationTokenSource cancellation)
    {
        try
        {
            return await operation;
        }
        catch
        {
            await cancellation.CancelAsync();
            throw;
        }
    }

    private async Task<SyncResult> RunWithDeviceAsync(
        SyncOptions options,
        Task<SourceLibrary> source,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Timeout);
        var mux = new UsbMuxClient(transport, timeout);
        var device = await options.SelectDeviceAsync(mux, cancellationToken);
        return await WirelessSession.RunAsync(
            mux,
            device,
            timeout,
            cancellationToken,
            token => this.RunOnDeviceAsync(
                options,
                source,
                mux,
                device,
                timeout,
                token));
    }

    private async Task<SyncResult> RunOnDeviceAsync(
        SyncOptions options,
        Task<SourceLibrary> sourceTask,
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var context = new SyncDeviceContext(
            mux,
            device,
            timeout,
            this.OpenConnection,
            error,
            options.Debug ? error : TextWriter.Null);

        using var journal = options.DryRun
            ? null
            : new SyncJournal(DeviceState.Root(options.StateFolder), device.Udid, allowPending: true);

        var executor = journal is null ? null : new SyncBatchExecutor(context, journal);
        if (journal is { HasPending: true })
        {
            // Recovery can modify the iPhone, so it must wait for a complete source inventory too.
            await sourceTask.WaitAsync(cancellationToken);
            await new SyncRecoveryRunner(context, journal, executor!).RunAsync(cancellationToken);
        }

        var (identity, catalogue, plan) = await new SyncPlanning(context, this.DeviceHashCacheDirectory)
            .CreateAsync(sourceTask, options, cancellationToken);

        var source = await sourceTask;

        using var workspace = new SyncWorkspace();
        var prepared = await new SyncSourcePreparation(fileSystem, this.PromptForGroupName, error)
            .PrepareAsync(
                source,
                plan,
                catalogue,
                workspace,
                cancellationToken);

        plan = prepared.Plan;
        if (options.DryRun || !plan.HasChanges) return new SyncResult(plan, options.DryRun, 0);

        ValidateWritePlan(
            source,
            plan,
            catalogue,
            options.AllowEmptySource);

        var transferBytes = checked(plan.TransferBytes
            + prepared.Additions.Sum(track => (long)(track.Metadata.Artwork?.Length ?? 0))
            + prepared.Artwork.Sum(track => (long)track.Metadata.Artwork!.Length));

        await using (var phone = await context.OpenAsync(cancellationToken))
        {
            await SyncDeviceContext.RequireSpaceAsync(phone.Afc, transferBytes, cancellationToken);
        }

        await error.WriteLineAsync($"Plan: add {plan.Add.Count}, delete {plan.Delete.Count}, "
            + $"repair {prepared.Artwork.Count} covers, unchanged {plan.Unchanged}.");

        var batches = await executor!.ExecutePlanAsync(
            identity,
            catalogue,
            prepared,
            cancellationToken);

        return new SyncResult(plan, false, batches);
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
        var hash = await afc.HashFileAsync(path, expected.Length, cancellationToken);
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
}
