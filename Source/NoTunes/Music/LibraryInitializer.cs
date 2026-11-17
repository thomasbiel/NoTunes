using System.Text.Json;
using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Platform;

namespace NoTunes.Music;

public sealed class LibraryInitializer(IUsbMuxTransport transport, TextWriter error)
{
    private const string StateFile = "initialization.json";
    private const int VerificationAttempts = 8;
    private static readonly TimeSpan VerificationRetryDelay = TimeSpan.FromMilliseconds(250);

    public async Task<InitializationResult> RunAsync(InitOptions options, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Timeout);
        var mux = new UsbMuxClient(transport, timeout);
        var device = DeviceOptions.SelectDevice(await mux.ListUsbDevicesAsync(cancellationToken), options.Device);
        await using var phone = await DeviceSyncConnection.OpenAsync(mux, device, timeout, cancellationToken);
        return await this.InitializeAsync(
            phone.Afc,
            phone.Atc,
            phone.Device.Name,
            device.Udid,
            options,
            cancellationToken);
    }

    internal async Task<InitializationResult> InitializeAsync(
        AfcClient afc,
        AtcClient atc,
        string name,
        string device,
        InitOptions options,
        CancellationToken cancellationToken)
    {
        using var journal = new SyncJournal(DeviceState.Root(options.StateFolder), device);
        var statePath = Path.Combine(journal.DirectoryPath, StateFile);
        var saved = File.Exists(statePath)
            ? JsonSerializer.Deserialize<InitializationState>(await File.ReadAllBytesAsync(statePath, cancellationToken))
                ?? throw new InvalidDataException("Invalid initialization record.")
            : null;
        var before = await ReadAsync(afc, cancellationToken);
        if (before.DatabaseId != 0)
        {
            if (saved is null || before.DatabaseId != saved.DatabaseId || before.ContainerId != saved.ContainerId)
                throw new IOException("This iPhone already belongs to a music library. Use sync; init will not replace it.");

            if (before.Anchor < SyncProtocol.FirstMediaRevision)
            {
                throw new IOException("The music library initialization is incomplete: "
                    + "Media revision 1 has not been committed.");
            }

            await RequirePreferencesAsync(afc, saved, cancellationToken);
            return new InitializationResult(device, options.DryRun, true, before.DatabaseId);
        }

        if (before.Anchor != SyncProtocol.UninitializedMediaRevision || before.Tracks.Count != 0)
            throw new IOException("Initialization requires an unsynced library at Media revision 0.");

        foreach (var path in new[] { SyncPaths.SavedCatalogue, ArtworkDatabase.Path })
        {
            if (await afc.GetFileInfoAsync(path, cancellationToken) is not null)
                throw new IOException("Initialization refuses existing host state: " + path);
        }

        if (await afc.GetFileInfoAsync(SyncPaths.MusicDirectory, cancellationToken) is not null
            && (await afc.ReadDirectoryAsync(SyncPaths.MusicDirectory, cancellationToken)).Count != 0)
            throw new IOException("Initialization refuses an occupied music directory.");

        if (saved is null)
        {
            foreach (var path in new[] { SyncPaths.Preferences, SyncPaths.PreferencesPlist })
            {
                if (await afc.GetFileInfoAsync(path, cancellationToken) is not null)
                    throw new IOException("Initialization refuses existing library preferences: " + path);
            }

            var ids = before.OccupiedIds.ToHashSet();
            long Allocate()
            {
                while (true)
                {
                    var id = LibraryPreferences.NewId();
                    if (ids.Add(id)) return id;
                }
            }

            var database = Allocate();
            var container = Allocate();
            saved = new InitializationState(
                LibraryPreferences.Create(),
                database,
                container,
                SyncOperations.Initialize(database, container, name),
                CatalogueCheckpoint.Capture(before));
        }

        SyncEngine.RequireUnchanged(saved.Before.Restore(), before);
        await CheckPreferenceOwnershipAsync(afc, saved, allowPartial: true, cancellationToken);
        if (options.DryRun) return new InitializationResult(device, true, false, saved.DatabaseId);

        DeviceState.Save(statePath, saved);
        await using var syncLock = await afc.AcquireSyncLockAsync(cancellationToken);
        SyncEngine.RequireUnchanged(before, await ReadAsync(afc, cancellationToken));
        var identity = LibraryIdentity.Parse(saved.Preferences);
        var session = new AtcSyncSession(atc, error);
        using var signer = await session.BeginAsync(identity, SyncProtocol.UninitializedMediaRevision, cancellationToken);
        if (saved.Signature is not null)
        {
            await DeviceStaging.RemoveMatchingAsync(afc, SyncPaths.Metadata, saved.Operation, cancellationToken);
            await DeviceStaging.RemoveMatchingAsync(
                afc,
                SyncPaths.Metadata + SyncPaths.SignatureSuffix,
                saved.Signature,
                cancellationToken);
        }

        await SyncEngine.RequireEmptyStagingAsync(afc, cancellationToken);
        saved = saved with { Signature = signer.Sign(saved.Operation) };
        DeviceState.Save(statePath, saved);
        await WritePreferenceAsync(afc, SyncPaths.Preferences, saved.Preferences, cancellationToken);
        await WritePreferenceAsync(afc, SyncPaths.PreferencesPlist, LibraryPreferences.Wrap(saved.Preferences),
            cancellationToken);
        await afc.EnsureDirectoryAsync(SyncPaths.MetadataDirectory, cancellationToken);
        await DeviceStaging.WriteVerifiedAsync(afc, SyncPaths.Metadata, saved.Operation, cancellationToken);
        await DeviceStaging.WriteVerifiedAsync(
            afc,
            SyncPaths.Metadata + SyncPaths.SignatureSuffix,
            saved.Signature,
            cancellationToken);
        await session.FinishMetadataAsync(SyncProtocol.UninitializedMediaRevision, cancellationToken);
        await session.TransferAsync(afc, [], new HashSet<long>(), cancellationToken);
        for (var attempt = 0; attempt < VerificationAttempts; attempt++)
        {
            try
            {
                var after = await ReadAsync(afc, cancellationToken);
                SyncEngine.VerifyResult(before with
                {
                    DatabaseId = saved.DatabaseId,
                    ContainerId = saved.ContainerId
                }, after, [], []);
                await RequirePreferencesAsync(afc, saved, cancellationToken);
                await error.WriteLineAsync("Verified new music library at Media revision 1.");
                return new InitializationResult(device, false, false, after.DatabaseId);
            }
            catch (IOException) when (attempt + 1 < VerificationAttempts)
            {
                await Task.Delay(VerificationRetryDelay, cancellationToken);
            }
        }

        throw new IOException("Initialization finished but its result could not be verified. Run init again.");
    }

    private static async Task WritePreferenceAsync(
        AfcClient afc,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (await afc.GetFileInfoAsync(path, cancellationToken) is not null)
        {
            var actual = await afc.ReadFileAsync(path, bytes.Length, cancellationToken);
            if (actual.AsSpan().SequenceEqual(bytes)) return;

            await DeviceStaging.RemoveMatchingAsync(afc, path, bytes, cancellationToken);
        }

        await DeviceStaging.WriteVerifiedAsync(afc, path, bytes, cancellationToken);
    }

    private static async Task RequirePreferencesAsync(
        AfcClient afc,
        InitializationState saved,
        CancellationToken cancellationToken)
    {
        await CheckPreferenceOwnershipAsync(afc, saved, allowPartial: false, cancellationToken);
        if (await LibraryIdentity.ReadAsync(afc, cancellationToken) != LibraryIdentity.Parse(saved.Preferences))
            throw new IOException("The initialized library identity changed.");
    }

    private static async Task CheckPreferenceOwnershipAsync(
        AfcClient afc,
        InitializationState saved,
        bool allowPartial,
        CancellationToken cancellationToken)
    {
        foreach (var (path, expected) in new[]
        {
            (SyncPaths.Preferences, saved.Preferences),
            (SyncPaths.PreferencesPlist, LibraryPreferences.Wrap(saved.Preferences))
        })
        {
            if (await afc.GetFileInfoAsync(path, cancellationToken) is null && allowPartial) continue;

            var bytes = await afc.ReadFileAsync(path, expected.Length, cancellationToken);
            if (!expected.AsSpan().StartsWith(bytes) || !allowPartial && bytes.Length != expected.Length)
                throw new IOException("Initialization preferences belong to a different operation: " + path);
        }
    }

    private static async Task<DeviceCatalogue> ReadAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        return await CatalogueReader.ReadAsync(await CatalogueSnapshot.CaptureAsync(afc, cancellationToken),
            cancellationToken, allowUninitialized: true);
    }
}
