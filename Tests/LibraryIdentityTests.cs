using System.Buffers.Binary;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class LibraryIdentityTests
{
    private const int PreferenceLength = 1232;
    private const int CatalogueOffset = 12;
    private const int UserOffset = 20;

    [Test]
    public void Reads_Raw_And_Wrapped_Preferences_Without_Losing_The_Full_Identity_Binding()
    {
        var raw = Preferences(0x1234, 0xab);
        var identity = LibraryIdentity.Parse(raw);
        Assert.That(identity.LibraryId, Is.EqualTo("000000000000129F"));
        Assert.That(LibraryIdentity.Parse(BinaryPlist.Write(Plist.Dict(("iPodPrefs", raw)))), Is.EqualTo(identity));
        Assert.That(LibraryIdentity.Parse(Plist.ToXml(Plist.Dict(("iPodPrefs", raw)))), Is.EqualTo(identity));
        var sameXor = LibraryIdentity.Parse(Preferences(0x1235, 0xaa));
        Assert.That(sameXor.LibraryId, Is.EqualTo(identity.LibraryId));
        Assert.That(sameXor.Binding, Is.Not.EqualTo(identity.Binding));
    }

    [TestCase(0UL, 2UL)]
    [TestCase(2UL, 0UL)]
    [TestCase(2UL, 2UL)]
    public void Rejects_Unconfigured_Identities(ulong catalogue, ulong user)
    {
        Assert.Throws<InvalidDataException>(() => LibraryIdentity.Parse(Preferences(catalogue, user)));
    }

    [Test]
    public async Task Rejects_Conflicting_Saved_Copies()
    {
        var peer = new AfcPeer();
        peer.Files[SyncPaths.Preferences] = Preferences(1, 2);
        peer.Files[SyncPaths.PreferencesPlist] = Plist.ToXml(Plist.Dict(("iPodPrefs", Preferences(2, 3))));
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryIdentity.ReadAsync(afc, CancellationToken.None));
    }

    private static byte[] Preferences(ulong catalogue, ulong user)
    {
        var result = new byte[PreferenceLength];
        "frpd"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 25);
        result[8] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(CatalogueOffset), catalogue);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(UserOffset), user);
        return result;
    }
}
