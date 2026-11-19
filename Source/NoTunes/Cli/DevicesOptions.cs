using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Devices, HelpText = "List Apple devices connected over USB or, with --wifi, Wi-Fi.")]
public sealed class DevicesOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        var mux = this.CreateMux(context);
        var devices = await this.ListDevicesAsync(mux, token);
        if (this.Device is not null) devices = [SelectDevice(devices, this.Device, this.Wifi || this.Address is not null)];

        if (this.Json)
        {
            WriteJson(context.Output, devices);
        }
        else if (devices.Count == 0)
        {
            await context.Output.WriteLineAsync(this.Wifi
                ? "No Wi-Fi Apple devices found. Specify --device <UDID> or DeviceUdid in app.config for Bonjour discovery, "
                    + "or use --address <IP> --device <UDID>."
                : "No USB Apple devices found. Connect and unlock the iPhone.");
        }
        else
        {
            foreach (var device in devices)
            {
                await context.Output.WriteLineAsync($"{device.Udid}  {device.ConnectionType}  product=0x{device.ProductId:X4}"
                    + (device.NetworkAddress is null ? string.Empty : $"  address={device.NetworkAddress}"));
            }
        }

        return ExitCodes.Success;
    }
}
