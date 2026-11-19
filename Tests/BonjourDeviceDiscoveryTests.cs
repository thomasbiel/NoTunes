using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class BonjourDeviceDiscoveryTests
{
    private const string Udid = "00008030-00112CE23A86402E";

    [Test]
    public async Task Tries_Other_Addresses_Until_The_Target_Authenticates([Values] bool authenticationFailure)
    {
        var attempted = new List<UsbDevice>();
        var discovery = new BonjourDeviceDiscovery(
            new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(1))
        {
            Browse = (_, _) => Task.FromResult<IReadOnlyList<IPEndPoint>>([
                Endpoint("192.168.1.41"), Endpoint("192.168.1.41"), Endpoint("192.168.1.42")]),
            Authenticate = (device, _) =>
            {
                attempted.Add(device);
                if (device.NetworkAddress == "192.168.1.41")
                {
                    throw authenticationFailure ? new AuthenticationException("Wrong certificate.")
                        : new IOException("Wrong UDID.");
                }

                return Task.CompletedTask;
            }
        };

        var result = await discovery.FindAsync(Udid.Replace("-", string.Empty), CancellationToken.None);

        Assert.That(attempted.Select(device => device.NetworkAddress),
            Is.EqualTo(new[] { "192.168.1.41", "192.168.1.42" }));
        Assert.That(attempted.All(device => device.Udid == Udid), Is.True);
        Assert.That(result.Single().NetworkAddress, Is.EqualTo("192.168.1.42"));
        Assert.That(result.Single().Udid, Is.EqualTo(Udid));
    }

    [Test]
    public async Task Reports_Advertised_Devices_That_Cannot_Be_Authenticated()
    {
        var discovery = new BonjourDeviceDiscovery(
            new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(1))
        {
            Browse = (_, _) => Task.FromResult<IReadOnlyList<IPEndPoint>>([Endpoint("192.168.1.42")]),
            Authenticate = (_, _) => throw new AuthenticationException("Wrong certificate.")
        };

        await Assert.ThatAsync(() => discovery.FindAsync(Udid, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("none could be authenticated"));
    }

    [Test]
    public async Task Uses_Addresses_From_Bonjour_Without_Assuming_It_Advertises_The_Lockdown_Port()
    {
        const int advertisedServicePort = 32498;
        var discovery = new BonjourDeviceDiscovery(
            new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(1))
        {
            Browse = (_, _) => Task.FromResult<IReadOnlyList<IPEndPoint>>([
                new IPEndPoint(IPAddress.Parse("192.168.1.42"), advertisedServicePort)]),
            Authenticate = (device, _) =>
            {
                Assert.That(device.NetworkAddress, Is.EqualTo("192.168.1.42"));
                return Task.CompletedTask;
            }
        };

        Assert.That(await discovery.FindAsync(Udid, CancellationToken.None), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Skips_Invalid_Addresses_And_Zero_Service_Ports()
    {
        var discovery = new BonjourDeviceDiscovery(
            new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(1))
        {
            Browse = (_, _) => Task.FromResult<IReadOnlyList<IPEndPoint>>([
                Endpoint("224.0.0.1"), Endpoint("127.0.0.1"), new IPEndPoint(IPAddress.Parse("192.168.1.42"), 0)]),
            Authenticate = (_, _) => throw new AssertionException("Unexpected authentication attempt.")
        };

        Assert.That(await discovery.FindAsync(Udid, CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task Continues_After_An_Address_Times_Out()
    {
        var discovery = new BonjourDeviceDiscovery(
            new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1)),
            TimeSpan.FromMilliseconds(50))
        {
            Browse = (_, _) => Task.FromResult<IReadOnlyList<IPEndPoint>>([
                Endpoint("192.168.1.41"), Endpoint("192.168.1.42")]),
            Authenticate = (device, token) => device.NetworkAddress == "192.168.1.41"
                ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask
        };

        var result = await discovery.FindAsync(Udid, CancellationToken.None);
        Assert.That(result.Single().NetworkAddress, Is.EqualTo("192.168.1.42"));
    }

    [Test]
    public async Task Preserves_Caller_Cancellation_During_Authentication()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = new BonjourDeviceDiscovery(
            new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(1))
        {
            Browse = (_, _) => Task.FromResult<IReadOnlyList<IPEndPoint>>([Endpoint("192.168.1.42")]),
            Authenticate = (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }
        };

        await Assert.ThatAsync(() => discovery.FindAsync(Udid, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Verifies_The_Udid_Inside_The_Paired_Tls_Session([Values] bool matching)
    {
        await LockdownTestPeer.RunAsync(
            _ => Plist.Dict((LockdownKeys.Value, Plist.Dict(
                (LockdownKeys.Udid, matching ? "test-device" : "other-device"),
                (LockdownKeys.DeviceName, "Test iPhone"),
                (LockdownKeys.ProductType, "iPhone12,8"),
                (LockdownKeys.ProductVersion, "27.2"),
                (LockdownKeys.BuildVersion, "test-build")))),
            async lockdown =>
            {
                if (matching)
                {
                    Assert.That((await lockdown.GetInfoAsync(CancellationToken.None)).Udid, Is.EqualTo("test-device"));
                }
                else
                {
                    await Assert.ThatAsync(() => lockdown.GetInfoAsync(CancellationToken.None),
                        Throws.TypeOf<IOException>().With.Message.Contains("identity does not match"));
                }
            },
            network: true);
    }

    private static IPEndPoint Endpoint(string address)
    {
        return new IPEndPoint(IPAddress.Parse(address), LockdownClient.LockdownPort);
    }
}
