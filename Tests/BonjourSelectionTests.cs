using NoTunes.Apple;
using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class BonjourSelectionTests
{
    private const string TargetUdid = "00008030-00112CE23A86402E";

    [Test]
    public async Task Falls_Back_To_Bonjour_When_The_Target_Is_Not_Listed([Values] bool otherDevicePresent)
    {
        var target = new UsbDevice(0, TargetUdid, 0) { NetworkAddress = "192.168.1.42" };
        var browsed = false;
        var mux = CreateMux(otherDevicePresent ? [NetworkDevice("other-device")] : [], (udid, _) =>
        {
            Assert.That(udid, Is.EqualTo(TargetUdid));
            browsed = true;
            return Task.FromResult<IReadOnlyList<UsbDevice>>([target]);
        });

        var options = new ProbeOptions { Wifi = true, Device = TargetUdid, Timeout = 1 };
        var selected = await options.SelectDeviceAsync(mux, CancellationToken.None);
        Assert.That(browsed, Is.True);
        Assert.That(selected, Is.SameAs(target));
    }

    [Test]
    public async Task Skips_Bonjour_When_The_Target_Is_Already_Reported()
    {
        var mux = CreateMux([NetworkDevice(TargetUdid.Replace("-", string.Empty))], (_, _) =>
            throw new AssertionException("Bonjour must not run when the multiplexer found the device."));

        var options = new ProbeOptions { Wifi = true, Device = TargetUdid, Timeout = 1 };
        Assert.That((await options.SelectDeviceAsync(mux, CancellationToken.None)).Udid, Is.EqualTo(TargetUdid));
    }

    [Test]
    public async Task Skips_Bonjour_For_Usb_And_Explicit_Addresses([Values] bool addressSpecified)
    {
        var mux = CreateMux([], (_, _) => throw new AssertionException("Unexpected Bonjour lookup."));
        var options = new ProbeOptions
        {
            Device = TargetUdid,
            Address = addressSpecified ? "192.168.1.42" : null,
            Timeout = 1
        };

        if (addressSpecified)
        {
            Assert.That((await options.SelectDeviceAsync(mux, CancellationToken.None)).NetworkAddress,
                Is.EqualTo(options.Address));
        }
        else
        {
            Assert.That(await options.ListDevicesAsync(mux, CancellationToken.None), Is.Empty);
        }
    }

    [Test]
    public async Task Does_Not_Guess_An_Identity_When_No_Udid_Is_Configured()
    {
        var mux = CreateMux([], (_, _) => throw new AssertionException("A pairing identity is required."));
        var options = new ProbeOptions { Wifi = true, Timeout = 1 };
        await Assert.ThatAsync(() => options.SelectDeviceAsync(mux, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("--device"));
    }

    private static UsbMuxClient CreateMux(
        object[] devices,
        Func<string, CancellationToken, Task<IReadOnlyList<UsbDevice>>> browse)
    {
        var reply = Plist.Dict((UsbMuxKeys.DeviceList, devices));
        return new UsbMuxClient(new ReplyUsbMuxTransport(new ScriptedStream(ProtocolFrames.UsbMux(1, reply))),
            TimeSpan.FromSeconds(1)) { BrowseBonjour = browse };
    }

    private static Dictionary<string, object> NetworkDevice(string udid)
    {
        // BSD sockaddr_in: length 16, family AF_INET, address 192.168.1.42.
        byte[] address = [16, 2, 0, 0, 192, 168, 1, 42, 0, 0, 0, 0, 0, 0, 0, 0];
        return Plist.Dict(
            (UsbMuxKeys.DeviceId, 1L),
            (UsbMuxKeys.Properties, Plist.Dict(
                (UsbMuxKeys.ConnectionType, "Network"),
                (UsbMuxKeys.SerialNumber, udid),
                (UsbMuxKeys.NetworkAddress, address))));
    }
}
