using System.Buffers.Binary;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class AfcWriteTests
{
    private const string Target = "Airlock/Media/123.mp3";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Uses_The_Shared_Apple_Sync_Lock_Without_Truncating_It()
    {
        const string sharedLock = "com.apple.itunes.lock_sync";
        var peer = new AfcPeer { AllowWrites = true };
        peer.Files[sharedLock] = [1, 2, 3];
        await using var afc = new AfcClient(peer, RequestTimeout);
        await using (await afc.AcquireSyncLockAsync(CancellationToken.None))
        {
            Assert.That(peer.Files.Keys, Is.EqualTo(new[] { sharedLock }));
            Assert.That(peer.Files[sharedLock], Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(peer.OpenHandles, Is.EqualTo(1));
        }

        Assert.That(peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Refuses_A_Sync_Lock_Held_By_Another_Apple_Client()
    {
        const string sharedLock = "com.apple.itunes.lock_sync";
        var peer = new AfcPeer { AllowWrites = true };
        peer.LockedPaths.Add(sharedLock);
        await using var afc = new AfcClient(peer, RequestTimeout);
        var exception = await Assert.ThrowsAsync<AfcException>(async () =>
            await afc.AcquireSyncLockAsync(CancellationToken.None));
        Assert.That(exception!.Code, Is.EqualTo(AfcPeer.LockWouldBlockStatus));
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Reports_An_Internal_Write_Close_Timeout_As_An_Io_Error()
    {
        var peer = new AfcPeer { AllowWrites = true, StallClose = true };
        await using var afc = new AfcClient(peer, RequestTimeout);
        using var input = new MemoryStream(new byte[] { 1 });
        var exception = await Assert.ThrowsAsync<IOException>(() => afc.WriteNewFileAsync(
            Target,
            input,
            input.Length,
            CancellationToken.None));
        Assert.That(exception!.InnerException, Is.InstanceOf<OperationCanceledException>());
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Reports_An_Internal_Sync_Lock_Release_Timeout_As_An_Io_Error()
    {
        var peer = new AfcPeer { AllowWrites = true, StallClose = true };
        await using var afc = new AfcClient(peer, RequestTimeout);
        var lease = await afc.AcquireSyncLockAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<IOException>(async () => await lease.DisposeAsync());
        Assert.That(exception!.InnerException, Is.InstanceOf<OperationCanceledException>());
        Assert.That(peer.WasDisposed, Is.True);
        await Assert.DoesNotThrowAsync(async () => await lease.DisposeAsync());
    }

    [Test]
    public async Task Preserves_User_Cancellation_When_Sync_Lock_Release_Times_Out()
    {
        using var cancellation = new CancellationTokenSource();
        var peer = new AfcPeer
        {
            AllowWrites = true,
            StallClose = true,
            OnClose = cancellation.Cancel
        };
        await using var afc = new AfcClient(peer, RequestTimeout);
        var lease = await afc.AcquireSyncLockAsync(cancellation.Token);
        await Assert.CatchAsync<OperationCanceledException>(async () => await lease.DisposeAsync());
        Assert.That(cancellation.IsCancellationRequested, Is.True);
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Preserves_User_Cancellation_When_Write_Cleanup_Times_Out()
    {
        using var cancellation = new CancellationTokenSource();
        var peer = new AfcPeer { AllowWrites = true, StallClose = true };
        await using var afc = new AfcClient(peer, RequestTimeout);
        using var input = new MemoryStream(new byte[] { 1 });
        var exception = await Assert.CatchAsync<OperationCanceledException>(() => afc.WriteNewFileAsync(
            Target,
            input,
            input.Length,
            cancellation.Token,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }));
        Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Preserves_User_Cancellation_During_Write_Close()
    {
        using var cancellation = new CancellationTokenSource();
        var peer = new AfcPeer
        {
            AllowWrites = true,
            StallClose = true,
            OnClose = cancellation.Cancel
        };
        await using var afc = new AfcClient(peer, RequestTimeout);
        using var input = new MemoryStream(new byte[] { 1 });
        await Assert.CatchAsync<OperationCanceledException>(() => afc.WriteNewFileAsync(
            Target,
            input,
            input.Length,
            cancellation.Token));
        Assert.That(cancellation.IsCancellationRequested, Is.True);
        Assert.That(peer.WasDisposed, Is.True);
    }

    [Test]
    public async Task Disposing_The_Sync_Lock_Is_Idempotent_And_Does_Not_Use_A_Closed_Connection()
    {
        var peer = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        var lease = await afc.AcquireSyncLockAsync(CancellationToken.None);
        await lease.DisposeAsync();
        await Assert.DoesNotThrowAsync(async () => await lease.DisposeAsync());
        var closedLease = await afc.AcquireSyncLockAsync(CancellationToken.None);
        await afc.DisposeAsync();
        await Assert.DoesNotThrowAsync(async () => await closedLease.DisposeAsync());
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
    public async Task Refuses_To_Overwrite_Existing_Files_Before_Opening()
    {
        var peer = new AfcPeer { AllowWrites = true };
        peer.Files[Target] = [7];
        using var input = new MemoryStream(new byte[] { 1 });
        var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => afc.WriteNewFileAsync(
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
        await Assert.ThrowsAsync<IOException>(() => afc.WriteNewFileAsync(
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
            await Assert.ThrowsAsync<AfcException>(() => afc.WriteNewFileAsync(
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
        await Assert.ThrowsAsync<IOException>(() => afc.RenameNewFileAsync(Target, destination, CancellationToken.None));
    }

    [TestCase("../escape")]
    [TestCase("/absolute")]
    [TestCase("Airlock/./file")]
    [TestCase("Airlock/file\0other")]
    public async Task Rejects_Unsafe_Write_Paths_Before_Any_Request(string path)
    {
        using var input = new MemoryStream();
        var peer = new AfcPeer { AllowWrites = true };
        var afc = new AfcClient(peer, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => afc.WriteNewFileAsync(
            path,
            input,
            0,
            CancellationToken.None));
        Assert.That(peer.Opens, Is.Zero);
    }
}
