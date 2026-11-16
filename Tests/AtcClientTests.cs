using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class AtcClientTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    [Test]
    public async Task Startup_Pings_The_Device_Answers_Pings_And_Reads_Music_Metrics()
    {
        await using var peer = new AtcPeer(
            Plist.Dict(("Command", "Ping"), ("Session", 37L)),
            MusicMetrics(Plist.Dict(("_Count", 42L), ("_PhysicalSize", 2048L))),
            Plist.Dict(("Command", "UnrelatedStartupMessage")),
            Permissions());
        await using var client = new AtcClient(peer, RequestTimeout);
        var status = await client.ReadSyncStatusAsync(CancellationToken.None);
        Assert.That(status.SyncAllowed, Is.True);
        Assert.That(status.DataProtected, Is.False);
        Assert.That(status.MusicTrackCount, Is.EqualTo(42));
        Assert.That(status.MusicBytes, Is.EqualTo(2048));
        var sent = peer.SentMessages();
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(sent[0].Text("Command"), Is.EqualTo("Ping"));
        Assert.That(sent[0].Number("Session"), Is.EqualTo(1));
        Assert.That(sent[1].Text("Command"), Is.EqualTo("Pong"));
        Assert.That(sent[1].Number("Session"), Is.EqualTo(37));
        Assert.That(peer.ResponsesRead, Is.EqualTo(4));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Reports_Blocked_Sync_For_Disabled_Music_Or_Protected_Data(bool protectedData)
    {
        await using var peer = new AtcPeer(Permissions(Plist.Dict(("ManualSync", true), ("DataProtected", protectedData), ("DisabledAssetTypes", protectedData ? Array.Empty<object>() : new object[] { "Music" }))));
        await using var client = new AtcClient(peer, RequestTimeout);
        var status = await client.ReadSyncStatusAsync(CancellationToken.None);
        Assert.That(status.SyncAllowed, Is.False);
        Assert.That(status.DataProtected, Is.EqualTo(protectedData));
        Assert.That(status.MusicTrackCount, Is.Null);
        Assert.That(status.MusicBytes, Is.Null);
    }

    [Test]
    public async Task Retains_Latest_Valid_Metrics_Across_Partial_Updates()
    {
        await using var peer = new AtcPeer(
            MusicMetrics(Plist.Dict(("_Count", 42L), ("_PhysicalSize", 1000L))),
            MusicMetrics(Plist.Dict(("_Count", 43L))),
            MusicMetrics(Plist.Dict(("_Count", "unavailable"), ("_PhysicalSize", 2000L))),
            Permissions());
        await using var client = new AtcClient(peer, RequestTimeout);
        var status = await client.ReadSyncStatusAsync(CancellationToken.None);
        Assert.That(status.MusicTrackCount, Is.EqualTo(43));
        Assert.That(status.MusicBytes, Is.EqualTo(2000));
    }

    [Test]
    public async Task Missing_Metrics_And_Wrongly_Typed_Optional_Metrics_Remain_Unknown()
    {
        await using var peer = new AtcPeer(
            Plist.Dict(("Command", "AssetMetrics")),
            Plist.Dict(("Command", "AssetMetrics"), ("Params", Plist.Dict())),
            Plist.Dict(("Command", "AssetMetrics"), ("Params", Plist.Dict(("Media", Plist.Dict())))),
            MusicMetrics(Plist.Dict(("_Count", "unknown"), ("_PhysicalSize", true))),
            Permissions());
        await using var client = new AtcClient(peer, RequestTimeout);
        var status = await client.ReadSyncStatusAsync(CancellationToken.None);
        Assert.That(status.MusicTrackCount, Is.Null);
        Assert.That(status.MusicBytes, Is.Null);
    }

    [TestCase("SyncFailed")]
    [TestCase("ConnectionInvalid")]
    public async Task Service_Failures_Stop_Startup_Immediately(string command)
    {
        await using var peer = new AtcPeer(Plist.Dict(("Command", command)), Permissions());
        await using var client = new AtcClient(peer, RequestTimeout);
        await Assert.ThatAsync(async () => await client.ReadSyncStatusAsync(CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains(command));
        Assert.That(peer.ResponsesRead, Is.EqualTo(1));
    }

    [TestCase("UnrelatedStartupMessage")]
    [TestCase("Ping")]
    public async Task Startup_Limit_Bounds_Unsolicited_Messages_Including_Pings(string command)
    {
        var startup = Plist.Dict(("Command", command), ("Session", 37L));
        var messages = Enumerable.Repeat(startup, 24).Append(Permissions()).ToArray();
        await using var peer = new AtcPeer(messages);
        await using var client = new AtcClient(peer, RequestTimeout);
        await Assert.ThatAsync(async () => await client.ReadSyncStatusAsync(CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("startup message limit"));
        Assert.That(peer.ResponsesRead, Is.EqualTo(24));
        Assert.That(peer.SentMessages(), Has.Count.EqualTo(command == "Ping" ? 25 : 1));
    }

    [Test]
    public async Task Accepts_Permissions_On_The_Last_Allowed_Startup_Frame()
    {
        var messages = Enumerable.Repeat(Plist.Dict(("Command", "UnrelatedStartupMessage")), 23).Append(Permissions()).ToArray();
        await using var peer = new AtcPeer(messages);
        await using var client = new AtcClient(peer, RequestTimeout);
        Assert.That((await client.ReadSyncStatusAsync(CancellationToken.None)).SyncAllowed, Is.True);
        Assert.That(peer.ResponsesRead, Is.EqualTo(24));
    }

    [TestCaseSource(nameof(MalformedMessages))]
    public async Task Rejects_Malformed_Startup_Messages(Dictionary<string, object> message)
    {
        await using var peer = new AtcPeer(message);
        await using var client = new AtcClient(peer, RequestTimeout);
        await Assert.ThatAsync(async () => await client.ReadSyncStatusAsync(CancellationToken.None), Throws.TypeOf<InvalidDataException>());
    }

    private static IEnumerable<TestCaseData> MalformedMessages()
    {
        yield return new TestCaseData(Plist.Dict()).SetName("Rejects_Missing_Atc_Command");
        yield return new TestCaseData(Plist.Dict(("Command", 1L))).SetName("Rejects_Non_String_Atc_Command");
        yield return new TestCaseData(Plist.Dict(("Command", "Ping"))).SetName("Rejects_Ping_Without_Session");
        yield return new TestCaseData(Plist.Dict(("Command", "Ping"), ("Session", "invalid"))).SetName("Rejects_Ping_With_Invalid_Session");
        yield return new TestCaseData(Plist.Dict(("Command", "SyncAllowed"))).SetName("Rejects_Missing_Sync_Permissions");
        yield return new TestCaseData(Plist.Dict(("Command", "SyncAllowed"), ("Params", "invalid"))).SetName("Rejects_Non_Dictionary_Sync_Permissions");
        yield return new TestCaseData(Permissions(Plist.Dict(("ManualSync", true), ("DataProtected", "true")))).SetName("Rejects_Invalid_Protection_Flag_On_The_Wire");
        yield return new TestCaseData(Permissions(Plist.Dict(("ManualSync", true), ("DisabledAssetTypes", "Music")))).SetName("Rejects_Invalid_Disabled_Assets_On_The_Wire");
        yield return new TestCaseData(Plist.Dict(("Command", "AssetMetrics"), ("Params", "invalid"))).SetName("Rejects_Non_Dictionary_Metrics");
    }

    [Test]
    public async Task Receive_Timeout_Is_An_Io_Error_And_Closes_The_Stream()
    {
        await using var peer = new AtcPeer
        {
            StallAtEnd = true
        };
        await using var client = new AtcClient(peer, TimeSpan.FromMilliseconds(100));
        await Assert.ThatAsync(async () => await client.ReadSyncStatusAsync(CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("Timed out"));
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Caller_Cancellation_During_Receive_Is_Preserved_And_Closes_The_Stream()
    {
        using var cancellation = new CancellationTokenSource();
        await using var peer = new AtcPeer
        {
            StallAtEnd = true,
            BeforeRead = cancellation.Cancel
        };
        await using var client = new AtcClient(peer, RequestTimeout);
        await Assert.ThatAsync(async () => await client.ReadSyncStatusAsync(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(cancellation.IsCancellationRequested, Is.True);
        Assert.That(peer.WasDisposed, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Disposing_The_Client_Closes_Its_Stream_After_Success_Or_Failure(bool fail)
    {
        await using var peer = new AtcPeer(fail ? Plist.Dict(("Command", "SyncFailed")) : Permissions());
        await using (var client = new AtcClient(peer, RequestTimeout))
        {
            if (fail)
                await Assert.ThatAsync(async () => await client.ReadSyncStatusAsync(CancellationToken.None), Throws.TypeOf<IOException>());
            else
                await client.ReadSyncStatusAsync(CancellationToken.None);
            Assert.That(peer.WasDisposed, Is.False);
        }

        Assert.That(peer.WasDisposed, Is.True);
    }

    private static Dictionary<string, object> Permissions(Dictionary<string, object>? parameters = null)
    {
        return Plist.Dict(("Command", "SyncAllowed"), ("Params", parameters ?? Plist.Dict(("ManualSync", true))));
    }

    private static Dictionary<string, object> MusicMetrics(Dictionary<string, object> values)
    {
        return Plist.Dict(("Command", "AssetMetrics"), ("Params", Plist.Dict(("Media", Plist.Dict(("Music", values))))));
    }
}
