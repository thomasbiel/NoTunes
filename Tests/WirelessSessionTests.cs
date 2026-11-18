using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class WirelessSessionTests
{
    [Test]
    public async Task Answers_Heartbeats_And_Renews_Assertions_Before_Releasing_Their_Predecessors()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new PlistPeer
        {
            OnMessage = message =>
            {
                Assert.That(message.Text("Command"), Is.EqualTo("Polo"));
                answered.TrySetResult();
            }
        };

        heartbeat.Queue(Plist.Dict(("Command", "Marco"), ("Interval", 10L)));
        var assertions = new List<PlistPeer>();
        var session = new WirelessSession(OpenServiceAsync, TimeSpan.FromSeconds(2), deadline.Token);
        await using (session)
        {
            await session.StartAsync(TimeSpan.FromMilliseconds(20), TimeSpan.Zero);
            await Task.WhenAll(answered.Task, renewed.Task).WaitAsync(deadline.Token);
            Assert.That(heartbeat.WasDisposed, Is.False);
        }

        Assert.That(heartbeat.WasDisposed, Is.True);
        Assert.That(assertions.All(peer => peer.WasDisposed), Is.True);

        Task<Stream> OpenServiceAsync(string service, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (service == "com.apple.mobile.heartbeat") return Task.FromResult<Stream>(heartbeat);

            Assert.That(service, Is.EqualTo("com.apple.mobile.assertion_agent"));
            Assert.That(heartbeat.WasDisposed, Is.False);
            var previous = assertions.LastOrDefault();
            var peer = new PlistPeer
            {
                OnMessage = message =>
                {
                    Assert.That(message.Text("CommandKey"), Is.EqualTo("CommandCreateAssertion"));
                    Assert.That(message.Text("AssertionTypeKey"), Is.EqualTo("AMDPowerAssertionTypeWirelessSync"));
                    Assert.That(message.Number("AssertionTimeoutKey"), Is.EqualTo(60));
                    Assert.That(previous?.WasDisposed, Is.Null.Or.False);
                    if (previous is not null) renewed.TrySetResult();
                }
            };

            peer.Queue([]);
            assertions.Add(peer);
            return Task.FromResult<Stream>(peer);
        }
    }

    [TestCase("SleepyTime")]
    [TestCase("Unexpected")]
    public async Task Cancels_The_Operation_If_The_Phone_Sleeps_Or_The_Heartbeat_Is_Invalid(string command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var heartbeat = new PlistPeer();
        using var assertion = new PlistPeer();
        assertion.Queue([]);
        await using var session = new WirelessSession(
            (service, _) => Task.FromResult<Stream>(service == DeviceServices.Heartbeat ? heartbeat : assertion),
            TimeSpan.FromSeconds(1), deadline.Token);

        await session.StartAsync(TimeSpan.FromHours(1), TimeSpan.Zero);
        heartbeat.Queue(Plist.Dict(("Command", command)));
        await Assert.ThatAsync(() => Task.Delay(Timeout.InfiniteTimeSpan, session.Token).WaitAsync(deadline.Token),
            Throws.InstanceOf<OperationCanceledException>());

        Assert.That(deadline.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task Cancels_The_Operation_If_An_Assertion_Cannot_Be_Renewed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var heartbeat = new PlistPeer();
        using var assertion = new PlistPeer();
        assertion.Queue([]);
        var requests = 0;
        await using var session = new WirelessSession((service, _) =>
        {
            if (service == DeviceServices.Heartbeat) return Task.FromResult<Stream>(heartbeat);
            if (++requests > 1) throw new IOException("Connection lost.");

            return Task.FromResult<Stream>(assertion);
        }, TimeSpan.FromSeconds(1), deadline.Token);

        await session.StartAsync(TimeSpan.FromMilliseconds(20), TimeSpan.Zero);
        await Assert.ThatAsync(() => Task.Delay(Timeout.InfiniteTimeSpan, session.Token).WaitAsync(deadline.Token),
            Throws.InstanceOf<OperationCanceledException>());

        Assert.That(deadline.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task Rejects_A_Power_Assertion_Error_And_Releases_The_Service()
    {
        using var heartbeat = new PlistPeer();
        using var assertion = new PlistPeer();
        assertion.Queue(Plist.Dict(("Error", "Denied")));
        await using var session = new WirelessSession(
            (service, _) => Task.FromResult<Stream>(service == DeviceServices.Heartbeat ? heartbeat : assertion),
            TimeSpan.FromSeconds(1), CancellationToken.None);

        await Assert.ThatAsync(() => session.StartAsync(TimeSpan.FromHours(1), TimeSpan.Zero),
            Throws.TypeOf<InvalidDataException>());

        Assert.That(assertion.WasDisposed, Is.True);
    }

    [Test]
    public void Serializes_Wifi_Commands_For_The_Same_Normalized_Udid()
    {
        var udid = Guid.NewGuid().ToString("D");
        using (WirelessSession.AcquireLease(udid))
        {
            Assert.That(() => WirelessSession.AcquireLease(udid.Replace("-", string.Empty).ToUpperInvariant()),
                Throws.TypeOf<IOException>().With.Message.Contains("Another NoTunes Wi-Fi command"));
        }

        string leasePath;
        using (var reacquired = WirelessSession.AcquireLease(udid))
        {
            leasePath = reacquired.Name;
            Assert.That(reacquired.CanWrite, Is.True);
        }

        File.Delete(leasePath);
        Directory.Delete(Path.GetDirectoryName(leasePath)!);
    }

    [Test]
    public async Task Usb_Operations_Do_Not_Start_A_Wireless_Session()
    {
        var mux = new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1));
        var result = await WirelessSession.RunAsync(
            mux,
            new UsbDevice(1, "test", 1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            _ => Task.FromResult(42));

        Assert.That(result, Is.EqualTo(42));
    }
}
