using CommandLine;
using NoTunes.Apple;

namespace NoTunes.Cli;

public abstract class DeviceOptions : CommandOptions
{
    private const int MinimumTimeoutSeconds = 1;
    private const int MaximumTimeoutSeconds = 300;
    private const int DefaultTimeoutSeconds = 15;

    [Option(CliSyntax.Device, MetaValue = CliSyntax.Udid, HelpText = "Select a USB iPhone by its UDID.")]
    public string? Device { get; set; }

    [Option(CliSyntax.Timeout, Default = DefaultTimeoutSeconds, MetaValue = CliSyntax.Seconds, HelpText = "Timeout per device request (1–300 seconds).")]
    public int Timeout { get; set; }

    [Option(CliSyntax.Json, HelpText = "Print the result as JSON.")]
    public bool Json { get; set; }

    protected UsbMuxClient CreateMux(CommandContext context)
    {
        this.ValidateTimeout();
        return new UsbMuxClient(context.Transport, TimeSpan.FromSeconds(this.Timeout));
    }

    protected void ValidateTimeout()
    {
        if (this.Timeout is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            throw new InvalidArgumentsException($"--{CliSyntax.Timeout} must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds.");
        }
    }

    public static UsbDevice SelectDevice(IReadOnlyList<UsbDevice> devices, string? udid)
    {
        if (udid is not null)
        {
            var normalized = DeviceUdid.Normalize(udid);
            return devices.SingleOrDefault(d => StringComparer.OrdinalIgnoreCase.Equals(DeviceUdid.Normalize(d.Udid), normalized)) ?? throw new IOException($"No USB device matches UDID '{udid}'.");
        }

        return devices.Count switch
        {
            0 => throw new IOException("No USB iPhone found. Connect it, unlock it and trust this computer."),
            1 => devices[0],
            _ => throw new IOException("Multiple USB Apple devices found. Select an iPhone with --device <UDID>.")
        };
    }
}
