using NoTunes.Apple;
using NoTunes.Cli;

namespace NoTunes.Music;

internal sealed class SyncPlanning(SyncDeviceContext context, string? cacheDirectory)
{
    public async Task<(LibraryIdentity Identity, DeviceCatalogue Catalogue, SyncPlan Plan)> CreateAsync(
        Task<SourceLibrary> sourceTask,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        await using var phone = await context.OpenAsync(cancellationToken);
        var identity = await LibraryIdentity.ReadAsync(phone.Afc, cancellationToken);
        var catalogue = await context.ReadCatalogueAsync(phone.Afc, cancellationToken);
        await SyncDeviceContext.ValidateSavedAnchorsAsync(phone.Afc, catalogue, cancellationToken);
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
                sourceTask.IsCompletedSuccessfully ? sourceTask.Result : null,
                catalogue,
                cancellationToken);
        }

        var source = await sourceTask.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await context.Error.WriteLineAsync($"Comparing {DisplayFormat.Number(source.Tracks.Count)} source tracks "
            + $"with {DisplayFormat.Number(catalogue.Tracks.Count)} local iPhone tracks...");

        var plan = await SyncPlanner.CreateAsync(
            context.Udid,
            source,
            catalogue,
            (track, token) => hashes.ReadAsync(phone.Afc, track, token),
            cancellationToken,
            context.Error);

        cancellationToken.ThrowIfCancellationRequested();
        hashes.Save();
        await context.Error.WriteLineAsync(
            $"Device hashes: {DisplayFormat.Number(hashes.Hits)} cached, "
            + $"{DisplayFormat.Number(hashes.Checks)} checked.");

        return (identity, catalogue, plan);
    }
}
