using System.Buffers.Binary;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class AfcBatchTests
{
    [Test]
    public async Task Pipelines_Attribute_Queries_In_Bounded_Batches()
    {
        const int count = AfcClient.MaximumFileInfoBatch * 2 + 2;
        var readCounts = new HashSet<int>();
        AfcPeer? peer = null;
        await using var files = peer = new AfcPeer
        {
            AllowPipelinedFileInfo = true,
            BeforeTransportRead = () => readCounts.Add(peer!.Operations.Count)
        };

        var paths = Enumerable.Range(0, count).Select(index => $"music/{index}.mp3").ToArray();
        foreach (var path in paths)
        {
            files.Files[path] = [1, 2, 3];
        }

        var progress = new List<long>();
        await using var afc = new AfcClient(files, TimeSpan.FromSeconds(2));
        var result = await afc.GetFileInfosAsync(paths, CancellationToken.None, progress.Add);
        Assert.That(result, Has.Count.EqualTo(count));
        Assert.That(result.Values.All(value => value![AfcKeys.FileSize] == "3"), Is.True);
        Assert.That(readCounts, Is.EquivalentTo([64, 128, count]));
        Assert.That(progress, Is.EqualTo(new long[] { 64, 128, count }));
        Assert.That(files.Opens, Is.Zero);
        Assert.That(files.WasDisposed, Is.False);
    }

    [Test]
    public async Task Drains_All_Replies_After_An_Afc_Error_And_Preserves_Connection_Alignment([Values] bool missing)
    {
        const ulong denied = 10;
        await using var stream = new ScriptedStream([
            .. Status(1, missing ? (ulong)AfcStatusCode.ObjectNotFound : denied),
            .. FileInfo(2), .. FileInfo(3)]);

        await using var afc = new AfcClient(stream, TimeSpan.FromSeconds(2));
        if (missing)
        {
            var result = await afc.GetFileInfosAsync(["first", "second"], CancellationToken.None);
            Assert.That(result["first"], Is.Null);
            Assert.That(result["second"]![AfcKeys.FileSize], Is.EqualTo("3"));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<AfcException>(
                () => afc.GetFileInfosAsync(["first", "second"], CancellationToken.None));

            Assert.That(exception!.Code, Is.EqualTo(denied));
        }

        Assert.That((await afc.GetFileInfoAsync("third", CancellationToken.None))![AfcKeys.FileSize], Is.EqualTo("3"));
        Assert.That(stream.WasDisposed, Is.False);
    }

    [TestCase(0ul)]
    [TestCase(2ul)]
    public async Task Wrong_Reply_Sequence_Closes_The_Connection(ulong sequence)
    {
        await using var stream = new ScriptedStream(FileInfo(sequence));
        await using var afc = new AfcClient(stream, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => afc.GetFileInfosAsync(["file"], CancellationToken.None));

        Assert.That(stream.WasDisposed, Is.True);
        Assert.That(afc.IsBroken, Is.True);
    }

    [Test]
    public async Task Closes_A_Stalled_Batch_On_Timeout_Or_Caller_Cancellation([Values] bool callerCancels)
    {
        var shortTimeout = TimeSpan.FromMilliseconds(50);
        using var cancellation = new CancellationTokenSource();
        if (callerCancels) cancellation.CancelAfter(shortTimeout);

        await using var stream = new SilentPeerStream();
        await using var afc = new AfcClient(stream, callerCancels ? TimeSpan.FromSeconds(5) : shortTimeout);
        if (callerCancels)
        {
            await Assert.CatchAsync<OperationCanceledException>(
                () => afc.GetFileInfosAsync(["file"], cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => afc.GetFileInfosAsync(["file"], cancellation.Token));
        }

        Assert.That(stream.WasDisposed, Is.True);
        Assert.That(afc.IsBroken, Is.True);
    }

    [Test]
    public async Task Validates_All_Paths_Before_Sending_Any_Requests()
    {
        await using var stream = new ScriptedStream([]);
        await using var afc = new AfcClient(stream, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => afc.GetFileInfosAsync(["valid", "../escape"], CancellationToken.None));

        Assert.That(stream.Written.Length, Is.Zero);
        Assert.That(stream.WasDisposed, Is.False);
    }

    private static byte[] Status(ulong id, ulong status)
    {
        var body = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(body, status);
        return ProtocolFrames.Afc(id, (ulong)AfcOperation.Status, body);
    }

    private static byte[] FileInfo(ulong id)
    {
        return ProtocolFrames.Afc(id, (ulong)AfcOperation.Data, Encoding.UTF8.GetBytes("st_size\0" + "3\0"));
    }
}
