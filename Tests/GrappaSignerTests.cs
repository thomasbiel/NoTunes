using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class GrappaSignerTests
{
    [Test]
    public void Matches_Independent_Signing_Vectors_Without_Reserializing_Operations()
    {
        foreach (var value in GrappaTestData.Vectors())
        {
            var vector = value.AsDict();
            Assert.That(GrappaSigner.HostRequest, Is.EqualTo(vector["request"]));
            using var signer = new GrappaSigner((byte[])vector["reply"]);
            var operation = (byte[])vector["operation"];
            Assert.That(signer.Sign(operation), Is.EqualTo(vector["sidecar"]));
            Assert.That(signer.Sign([.. operation, 0]), Is.Not.EqualTo(vector["sidecar"]));
        }
    }

    [TestCase(0)]
    [TestCase(82)]
    [TestCase(84)]
    public void Rejects_Unexpected_Reply_Lengths(int length)
    {
        Assert.Throws<InvalidDataException>(() => new GrappaSigner(new byte[length]));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(18)]
    public void Rejects_Unexpected_Reply_Envelopes(int offset)
    {
        var reply = (byte[])GrappaTestData.Vectors()[0].AsDict()["reply"];
        reply[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => new GrappaSigner(reply));
    }

    [Test]
    public void Rejects_Empty_And_Oversized_Operation_Files()
    {
        using var signer = new GrappaSigner(GrappaTestData.Reply());
        Assert.Throws<InvalidDataException>(() => signer.Sign([]));
        Assert.Throws<InvalidDataException>(() => signer.Sign(new byte[SyncProtocol.MaxOperationBytes + 1]));
    }

}
