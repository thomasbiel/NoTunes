using System.Net;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class BonjourRecordsTests
{
    [Test]
    public void Resolves_Split_And_Reordered_Answers_And_Removes_Goodbyes()
    {
        var records = new BonjourRecords();
        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Address())).Read(7));
        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Srv())).Read(7));
        Assert.That(records.Endpoints(), Is.Empty, "An address and SRV need an Apple service PTR.");
        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Ptr())).Read(7));

        Assert.That(records.Endpoints(), Is.EqualTo(new[]
        {
            new IPEndPoint(IPAddress.Parse("192.168.1.42"), LockdownClient.LockdownPort)
        }));

        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Ptr(ttl: 0))).Read(7));
        Assert.That(records.Endpoints(), Is.Empty);
    }

    [Test]
    public void Queries_The_Instance_And_Its_Missing_Host_Addresses()
    {
        var records = new BonjourRecords();
        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Ptr())).Read(7));
        Assert.That(records.Questions(), Does.Contain(new MdnsQuestion(MdnsTestData.Instance, MdnsRecordType.Srv)));

        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Srv())).Read(7));
        Assert.That(records.Questions(), Does.Contain(new MdnsQuestion(MdnsTestData.Host, MdnsRecordType.A)));
        Assert.That(records.Questions(), Does.Contain(new MdnsQuestion(MdnsTestData.Host, MdnsRecordType.Aaaa)));

        records.Add(new MdnsPacket(MdnsTestData.Response(MdnsTestData.Address())).Read(7));
        Assert.That(records.Questions(), Does.Not.Contain(new MdnsQuestion(MdnsTestData.Host, MdnsRecordType.A)));
    }

    [TestCase("0.0.0.0")]
    [TestCase("127.0.0.1")]
    [TestCase("224.0.0.1")]
    [TestCase("::")]
    [TestCase("::1")]
    [TestCase("ff02::1")]
    public void Rejects_Non_Device_Addresses(string address)
    {
        var records = new BonjourRecords();
        records.Add(new MdnsPacket(MdnsTestData.Response(
            MdnsTestData.Ptr(), MdnsTestData.Srv(), MdnsTestData.Address(address))).Read(7));

        Assert.That(records.Endpoints(), Is.Empty);
    }
}
