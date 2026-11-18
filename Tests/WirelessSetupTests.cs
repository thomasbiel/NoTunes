using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class WirelessSetupTests
{
    [Test]
    public async Task Enables_Only_Wifi_Sync_And_Verifies_That_The_Setting_Was_Retained([Values] bool retained)
    {
        var writes = 0;
        await LockdownTestPeer.RunAsync(request =>
        {
            Assert.That(request.Text("Domain"), Is.EqualTo("com.apple.mobile.wireless_lockdown"));
            var key = request.Text("Key");
            if (request.Text("Request") == "SetValue")
            {
                Assert.That(key, Is.EqualTo("EnableWifiConnections"));
                Assert.That(request["Value"], Is.True);
                writes++;
                return [];
            }

            Assert.That(request.Text("Request"), Is.EqualTo("GetValue"));
            return key switch
            {
                "SupportsWifiSyncing" => Plist.Dict(("Value", true)),
                "EnableWifiConnections" => Plist.Dict(("Value", retained)),
                _ => throw new AssertionException($"Unexpected wireless setting: {key}")
            };
        }, async lockdown =>
        {
            if (retained)
            {
                await lockdown.EnableWifiSyncAsync(CancellationToken.None);
            }
            else
            {
                await Assert.ThatAsync(() => lockdown.EnableWifiSyncAsync(CancellationToken.None),
                    Throws.TypeOf<IOException>().With.Message.Contains("did not retain"));
            }
        });

        Assert.That(writes, Is.EqualTo(1));
    }

    [Test]
    public async Task Does_Not_Change_Unsupported_Devices()
    {
        await LockdownTestPeer.RunAsync(request =>
        {
            Assert.That(request.Text("Request"), Is.EqualTo("GetValue"));
            Assert.That(request.Text("Key"), Is.EqualTo("SupportsWifiSyncing"));
            return Plist.Dict(("Value", false));
        }, async lockdown =>
        {
            await Assert.ThatAsync(() => lockdown.EnableWifiSyncAsync(CancellationToken.None),
                Throws.TypeOf<IOException>().With.Message.Contains("does not support"));
        });
    }

    [Test]
    public async Task Refuses_Unencrypted_Wifi_Services_Without_Connecting_To_Their_Ports()
    {
        await LockdownTestPeer.RunAsync(request =>
        {
            Assert.That(request.Text("Request"), Is.EqualTo("StartService"));
            return Plist.Dict(("Port", 1234L), ("EnableServiceSSL", false));
        }, async lockdown =>
        {
            await Assert.ThatAsync(() => lockdown.OpenServiceAsync(DeviceServices.Afc, CancellationToken.None),
                Throws.TypeOf<IOException>().With.Message.Contains("did not enable TLS"));
        }, network: true);
    }
}
