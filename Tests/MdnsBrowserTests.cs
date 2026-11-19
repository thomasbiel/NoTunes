using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class MdnsBrowserTests
{
    [Test]
    public async Task Resolves_A_Service_With_Followup_Queries_And_Ignores_Malformed_And_Wrong_Port_Replies()
    {
        using var channel = new SimulatedMdnsChannel(
            SimulatedMdnsChannel.Reply([1, 2, 3]),
            SimulatedMdnsChannel.Reply(MdnsTestData.Response(MdnsTestData.Ptr()), port: 1234),
            SimulatedMdnsChannel.Reply(MdnsTestData.Response(MdnsTestData.Ptr())),
            SimulatedMdnsChannel.Reply(MdnsTestData.Response(MdnsTestData.Srv())),
            SimulatedMdnsChannel.Reply(MdnsTestData.Response(MdnsTestData.Address())));

        var result = await MdnsBrowser.BrowseAsync(channel, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.That(result.Single().Address.ToString(), Is.EqualTo("192.168.1.42"));
        Assert.That(channel.Sent,
            Does.Contain(MdnsPacket.Query(new MdnsQuestion(MdnsTestData.Instance, MdnsRecordType.Srv))));

        Assert.That(channel.Sent,
            Does.Contain(MdnsPacket.Query(new MdnsQuestion(MdnsTestData.Host, MdnsRecordType.A))));
    }

    [Test]
    public async Task Returns_An_Empty_Result_When_Browsing_Times_Out()
    {
        using var channel = new SimulatedMdnsChannel();
        var result = await MdnsBrowser.BrowseAsync(channel, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Preserves_Caller_Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var channel = new SimulatedMdnsChannel { OnSend = _ => cancellation.Cancel() };
        await Assert.ThatAsync(() => MdnsBrowser.BrowseAsync(channel, TimeSpan.FromSeconds(10), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }
}
