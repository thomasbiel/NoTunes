using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class SyncDeviceContext(
    UsbMuxClient mux,
    UsbDevice device,
    TimeSpan timeout,
    SyncConnectionFactory openConnection,
    TextWriter error,
    TextWriter debug)
{
    public string Udid => device.Udid;

    public TextWriter Error => error;

    public TextWriter Debug => debug;

    public Task<IDeviceSyncConnection> OpenAsync(CancellationToken cancellationToken)
    {
        return openConnection(
            mux,
            device,
            timeout,
            cancellationToken);
    }

    public static async Task RequireSpaceAsync(AfcClient afc, long bytes, CancellationToken cancellationToken)
    {
        var storage = await afc.GetDeviceInfoAsync(cancellationToken);
        if (!ProtocolNumbers.TryParseNonNegativeInt64(storage.GetValueOrDefault(AfcKeys.FreeBytes), out var free)
            || free < checked(bytes + SyncProtocol.MetadataReserveBytes))
        {
            throw new IOException("Insufficient verified iPhone free space for additions and the metadata reserve.");
        }
    }

    public static async Task ValidateSavedAnchorsAsync(
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

    public async Task<DeviceCatalogue> ReadCatalogueAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        var snapshot = await CatalogueSnapshot.CaptureWithRetryAsync(afc, cancellationToken, error);
        var catalogue = await CatalogueReader.ReadAsync(snapshot, cancellationToken);
        return catalogue with
        {
            ArtworkHighWaterMark = await ArtworkDatabase.HighestAsync(
                afc, catalogue.ArtworkHighWaterMark, cancellationToken)
        };
    }
}
