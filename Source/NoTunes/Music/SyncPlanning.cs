using NoTunes.Apple;
using NoTunes.Cli;

namespace NoTunes.Music;

internal sealed class SyncPlanning(SyncDeviceContext context, string? cacheDirectory)
{
    public async Task<(LibraryIdentity Identity, DeviceCatalogue Catalogue, SyncPlan Plan)> CreateAsync(
        SourceLibrary source,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        await using var phone = await context.OpenAsync(cancellationToken);
        var identity = await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken);
        var catalogue = await context.ReadCatalogueAsync(phone.Afc, cancellationToken);
        await SyncDeviceContext.ValidateSavedAnchorsAsync(phone.Afc, catalogue, cancellationToken);
        await context.Error.WriteLineAsync($"Comparing {source.Tracks.Count} source tracks "
            + $"with {catalogue.Tracks.Count} local iPhone tracks...");

        var hashes = DeviceHashCache.Open(
            cacheDirectory,
            context.Udid,
            identity,
            catalogue,
            options.FullReadback,
            context.Error);

        hashes.Debug = context.Debug;
        if (cacheDirectory is not null)
        {
            await hashes.PrefetchAsync(
                phone.Afc,
                source,
                catalogue,
                cancellationToken);
        }

        var plan = await SyncPlanner.CreateAsync(
            context.Udid,
            source,
            catalogue,
            (track, token) => hashes.ReadAsync(phone.Afc, track, token),
            cancellationToken,
            context.Error);

        cancellationToken.ThrowIfCancellationRequested();
        hashes.Save();
        await context.Error.WriteLineAsync($"Device hashes: {hashes.Hits} cached, {hashes.Reads} read from iPhone.");
        return (identity, catalogue, plan);
    }
}
