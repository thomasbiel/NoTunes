using System.Buffers.Binary;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class AfcWriteTests
{
    private const string Target = "Airlock/Media/123.mp3";

    [Test]
    public async Task Disposing_The_Sync_Lock_Is_Idempotent_And_Does_Not_Use_A_Closed_Connection()
    {
        var peer = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        var lease = await afc.AcquireSyncLockAsync(CancellationToken.None);
        await lease.DisposeAsync();
        Assert.DoesNotThrowAsync(async () => await lease.DisposeAsync());
        var closedLease = await afc.AcquireSyncLockAsync(CancellationToken.None);
        await afc.DisposeAsync();
        Assert.DoesNotThrowAsync(async () => await closedLease.DisposeAsync());
    }

    [Test]
    public async Task Writes_In_Bounded_Chunks_With_A_Separate_Protocol_Header_And_Verifies_Exact_Bytes()
    {
        var bytes = Enumerable.Range(0, 1024 * 1024 + 17).Select(i => (byte)i).ToArray();
        var peer = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        using var input = new MemoryStream(bytes);
        await afc.WriteNewFileAsync(
            Target,
            input,
            bytes.Length,
            CancellationToken.None);
        Assert.That(peer.Files[Target], Is.EqualTo(bytes));
        Assert.That(peer.WritePackets, Has.Count.EqualTo(2));
        foreach (var packet in peer.WritePackets)
        {
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(16)), Is.EqualTo(48UL));
        }

        Assert.That(peer.OpenHandles, Is.Zero);
        Assert.That(await afc.ReadFileAsync(Target, bytes.Length, CancellationToken.None), Is.EqualTo(bytes));
    }

    [Test]
    public void Refuses_To_Overwrite_Existing_Files_Before_Opening()
    {
        var peer = new AfcPeer { AllowWrites = true };
        peer.Files[Target] = [7];
        using var input = new MemoryStream(new byte[] { 1 });
        var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        Assert.ThrowsAsync<IOException>(() => afc.WriteNewFileAsync(
            Target,
            input,
            1,
            CancellationToken.None));
        Assert.That(peer.Opens, Is.Zero);
        Assert.That(peer.Files[Target], Is.EqualTo(new byte[] { 7 }));
    }

    [TestCase(0)]
    [TestCase(2)]
    public async Task Closes_The_Handle_When_The_Source_Size_Is_Wrong(int size)
    {
        var peer = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        using var input = new MemoryStream(new byte[] { 1 });
        Assert.ThrowsAsync<IOException>(() => afc.WriteNewFileAsync(
            Target,
            input,
            size,
            CancellationToken.None));
        Assert.That(peer.OpenHandles, Is.Zero);
        Assert.That(peer.Closes, Is.EqualTo(1));
    }

    [Test]
    public async Task Reports_Write_And_Close_Failures()
    {
        foreach (var peer in new[] { new AfcPeer { AllowWrites = true, FailWrite = true }, new AfcPeer { AllowWrites = true, FailClose = true } })
        {
            await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
            using var input = new MemoryStream(new byte[] { 1 });
            Assert.ThrowsAsync<AfcException>(() => afc.WriteNewFileAsync(
                Target,
                input,
                1,
                CancellationToken.None));
            Assert.That(peer.OpenHandles, Is.Zero);
        }
    }

    [Test]
    public async Task Renames_Only_When_The_Destination_Is_Absent()
    {
        var peer = new AfcPeer { AllowWrites = true };
        peer.Files[Target] = [7];
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        var destination = Target[..^4];
        await afc.RenameNewFileAsync(Target, destination, CancellationToken.None);
        Assert.That(peer.Files[destination], Is.EqualTo(new byte[] { 7 }));
        Assert.That(peer.Files.ContainsKey(Target), Is.False);
        peer.Files[Target] = [8];
        Assert.ThrowsAsync<IOException>(() => afc.RenameNewFileAsync(Target, destination, CancellationToken.None));
    }

    [TestCase("../escape")]
    [TestCase("/absolute")]
    [TestCase("Airlock/./file")]
    [TestCase("Airlock/file\0other")]
    public void Rejects_Unsafe_Write_Paths_Before_Any_Request(string path)
    {
        using var input = new MemoryStream();
        var peer = new AfcPeer { AllowWrites = true };
        var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        Assert.ThrowsAsync<InvalidDataException>(() => afc.WriteNewFileAsync(
            path,
            input,
            0,
            CancellationToken.None));
        Assert.That(peer.Opens, Is.Zero);
    }
}
