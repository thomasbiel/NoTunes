namespace NoTunes.Apple;

internal interface IDeviceSyncConnection : IAsyncDisposable
{
    AfcClient Afc { get; }

    AtcClient Atc { get; }
}
