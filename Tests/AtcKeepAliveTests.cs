using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class AtcKeepAliveTests
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly LibraryIdentity Identity = new("0011223344556677", "binding");

    [Test]
    public async Task Disposal_Cancels_An_In_Flight_Keepalive_Send()
    {
        var stream = new PausingWriteStream();
        await using var client = new AtcClient(stream, RequestTimeout, KeepAliveInterval);
        client.StartKeepAlive(CancellationToken.None);
        await stream.FirstWriteStarted.Task.WaitAsync(RequestTimeout);
        await client.DisposeAsync().AsTask().WaitAsync(RequestTimeout);
        Assert.That(stream.Written, Is.Not.Empty);
        Assert.That(stream.ResumeFirstWrite.Task.IsCompleted, Is.False);
    }

    [Test]
    public async Task Keeps_The_Current_Session_Alive_Before_Metadata_Is_Finished()
    {
        var ping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new AtcPeer(SyncTestData.SessionMessages())
        {
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) == AtcCommands.Ping
                    && message.Number(AtcKeys.Session) == SyncProtocol.Session)
                {
                    ping.TrySetResult();
                }
            }
        };

        await using var client = new AtcClient(peer, RequestTimeout, KeepAliveInterval);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await ping.Task.WaitAsync(RequestTimeout);
        await client.DisposeAsync();
        Assert.That(peer.WasDisposed, Is.True);
        Assert.That(peer.SentMessages().Any(message =>
            message.Text(AtcKeys.Command) == AtcCommands.FinishedMetadata), Is.False);

        await Assert.DoesNotThrowAsync(async () => await client.DisposeAsync());
    }

    [Test]
    public async Task Keeps_The_Atc_Link_Alive_While_Afc_Verification_Is_Blocked([Values] bool deviceHash)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout);
        var track = SyncTestData.Track();
        var readbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readbackPing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))),
            SyncTestData.Message(AtcCommands.SyncFinished)))
        {
            BeforeFrameAsync = (frame, token) => frame >= 4 ? complete.Task.WaitAsync(token) : Task.CompletedTask,
            OnMessageSent = message =>
            {
                var command = message.Text(AtcKeys.Command);
                if (command == AtcCommands.Ping && readbackStarted.Task.IsCompleted)
                {
                    Assert.That(message.Number(AtcKeys.Session), Is.EqualTo(SyncProtocol.Session));
                    readbackPing.TrySetResult();
                }

                if (command == AtcCommands.FileComplete)
                {
                    complete.TrySetResult();
                }
            }
        };

        async Task WaitForVerificationAsync(CancellationToken token)
        {
            readbackStarted.TrySetResult();
            await readbackPing.Task.WaitAsync(token);
        }

        var files = new AfcPeer
        {
            AllowWrites = true,
            FileHashError = deviceHash ? null : AfcStatusCode.OperationNotSupported,
            BeforeFileReadReplyAsync = deviceHash ? null : WaitForVerificationAsync,
            BeforeFileHashReplyAsync = deviceHash ? WaitForVerificationAsync : null
        };

        await using var afc = new AfcClient(files, RequestTimeout);
        await using var client = new AtcClient(peer, RequestTimeout, KeepAliveInterval);
        await client.ReadSyncStatusAsync(deadline.Token);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, deadline.Token);
        await session.FinishMetadataAsync(7, deadline.Token);
        await session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            deadline.Token);

        Assert.That(readbackPing.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(files.Files[SyncPaths.Airlock + track.AssetId], Is.EqualTo(File.ReadAllBytes(track.LocalPath)));
    }
}
