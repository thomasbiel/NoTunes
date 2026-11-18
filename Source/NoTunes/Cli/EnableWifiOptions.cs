using CommandLine;
using NoTunes.Apple;

namespace NoTunes.Cli;

[Verb(CliSyntax.EnableWifi, HelpText = "Enable Wi-Fi synchronization on an unlocked, USB-connected, trusted iPhone.")]
public sealed class EnableWifiOptions : DeviceOptions
{
    [Value(0, Hidden = true)]
    public IEnumerable<string> AdditionalArguments { get; set; } = [];

    public override async Task<int> Execute(CommandContext context, CancellationToken token)
    {
        ValidateAdditionalArguments(this.AdditionalArguments);
        if (this.Wifi || this.Address is not null)
        {
            throw new InvalidArgumentsException("enable-wifi requires USB; omit --wifi and --address.");
        }

        var mux = this.CreateMux(context);
        var device = await this.SelectDeviceAsync(mux, token);
        await using var lockdown = await LockdownClient.ConnectAsync(
            mux,
            device,
            TimeSpan.FromSeconds(this.Timeout),
            token);

        await lockdown.StartPairedSessionAsync(token);
        var info = await lockdown.GetInfoAsync(token);
        await lockdown.EnableWifiSyncAsync(token);
        if (this.Json)
        {
            WriteJson(context.Output, new { Device = info, WifiEnabled = true });
        }
        else
        {
            await context.Output.WriteLineAsync($"Wi-Fi synchronization enabled for {info.Name} ({info.Udid}).");
        }

        return ExitCodes.Success;
    }
}
