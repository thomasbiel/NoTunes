namespace NoTunes.Apple;

public sealed record UsbDevice(uint DeviceId, string Udid, long ProductId)
{
    public string? NetworkAddress { get; init; }

    public string ConnectionType => this.NetworkAddress is null ? "USB" : "Network";
}
