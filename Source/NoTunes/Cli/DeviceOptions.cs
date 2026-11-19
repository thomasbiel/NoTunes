using CommandLine;
using NoTunes.Apple;
using System.Net;

namespace NoTunes.Cli;

public abstract class DeviceOptions : CommandOptions
{
    private const int MinimumTimeoutSeconds = 1;
    private const int MaximumTimeoutSeconds = 300;
    private const int DefaultTimeoutSeconds = 15;

    [Option(CliSyntax.Device, MetaValue = CliSyntax.Udid, HelpText = "Select an iPhone by its UDID; defaults to DeviceUdid in app.config.")]
    public string? Device { get; set; }

    [Option(CliSyntax.Wifi, HelpText = "Use Wi-Fi instead of USB; requires an existing trusted pairing.")]
    public bool Wifi { get; set; }

    [Option(CliSyntax.Address, MetaValue = CliSyntax.IpAddress, HelpText = "Connect directly to this iPhone IP over Wi-Fi; with --wifi defaults to DeviceAddress in app.config.")]
    public string? Address { get; set; }

    [Option(CliSyntax.Timeout, Default = DefaultTimeoutSeconds, MetaValue = CliSyntax.Seconds, HelpText = "Timeout per device request (1–300 seconds).")]
    public int Timeout { get; set; }

    [Option(CliSyntax.Json, HelpText = "Print the result as JSON.")]
    public bool Json { get; set; }

    protected UsbMuxClient CreateMux(CommandContext context)
    {
        this.ValidateDeviceOptions();
        return new UsbMuxClient(context.Transport, TimeSpan.FromSeconds(this.Timeout));
    }

    protected void ValidateDeviceOptions()
    {
        if (this.Timeout is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            throw new InvalidArgumentsException($"--{CliSyntax.Timeout} must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds.");
        }

        if (this.Address is not null &&
            (!IPAddress.TryParse(this.Address, out var address) || !DeviceNetworkAddress.IsUnicast(address)))
        {
            throw new InvalidArgumentsException("--address requires a unicast IPv4 or IPv6 address.");
        }

        if (this.Address is not null && string.IsNullOrWhiteSpace(this.Device))
        {
            throw new InvalidArgumentsException("--address requires --device <UDID> to select the trusted pairing.");
        }
    }

    internal Task<IReadOnlyList<UsbDevice>> ListDevicesAsync(UsbMuxClient mux, CancellationToken token)
    {
        this.ValidateDeviceOptions();
        if (this.Address is not null)
        {
            IReadOnlyList<UsbDevice> devices = [new UsbDevice(0, DeviceUdid.Format(this.Device!), 0)
            {
                NetworkAddress = IPAddress.Parse(this.Address).ToString()
            }];

            return Task.FromResult(devices);
        }

        return mux.FindDevicesAsync(this.Wifi, this.Device, token);
    }

    internal async Task<UsbDevice> SelectDeviceAsync(UsbMuxClient mux, CancellationToken token)
    {
        return SelectDevice(await this.ListDevicesAsync(mux, token), this.Device, this.Wifi || this.Address is not null);
    }

    public static UsbDevice SelectDevice(IReadOnlyList<UsbDevice> devices, string? udid, bool network = false)
    {
        var connection = network ? "Wi-Fi" : "USB";
        if (udid is not null)
        {
            var normalized = DeviceUdid.Normalize(udid);
            return devices.FirstOrDefault(d =>
                StringComparer.OrdinalIgnoreCase.Equals(DeviceUdid.Normalize(d.Udid), normalized))
                ?? throw new IOException(network
                    ? $"No Wi-Fi device matches UDID '{udid}' after Apple service and Bonjour discovery. "
                        + "Unlock the paired iPhone on the same network, enable Wi-Fi sync, or use --address <IP>."
                    : $"No {connection} device matches UDID '{udid}'.");
        }

        var distinctDevices = devices.DistinctBy(d => DeviceUdid.Normalize(d.Udid), StringComparer.OrdinalIgnoreCase).ToArray();
        return distinctDevices.Length switch
        {
            0 => throw new IOException(network
                ? "No Wi-Fi iPhone found. Specify --device <UDID> or DeviceUdid in app.config for Bonjour discovery, "
                    + "or use --address <IP> --device <UDID>."
                : "No USB iPhone found. Connect it, unlock it and trust this computer."),
            1 => distinctDevices[0],
            _ => throw new IOException($"Multiple {connection} Apple devices found. Select an iPhone with --device <UDID>.")
        };
    }
}
