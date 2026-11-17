using CommandLine;

namespace NoTunes.Cli;

[Verb(CliSyntax.Devices, HelpText = "List Apple devices connected over USB.")]
public sealed class DevicesOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        var mux = this.CreateMux(context);
        var devices = await mux.ListUsbDevicesAsync(token);
        if (this.Device is not null) devices = [SelectDevice(devices, this.Device)];

        if (this.Json)
        {
            WriteJson(context.Output, devices);
        }
        else if (devices.Count == 0)
        {
            await context.Output.WriteLineAsync("No USB Apple devices found. Connect and unlock the iPhone.");
        }
        else
        {
            foreach (var device in devices)
            {
                await context.Output.WriteLineAsync($"{device.Udid}  USB  product=0x{device.ProductId:X4}");
            }
        }

        return ExitCodes.Success;
    }
}
