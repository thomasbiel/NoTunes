namespace NoTunes.Apple;

internal delegate Task<IDeviceSyncConnection> SyncConnectionFactory(
    UsbMuxClient mux,
    UsbDevice device,
    TimeSpan timeout,
    CancellationToken cancellationToken);
