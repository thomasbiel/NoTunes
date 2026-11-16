using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class AtcSyncSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly LibraryIdentity Identity = new("0011223344556677", "binding");

    [Test]
    public async Task Caller_Cancellation_Stops_The_Control_Reader_And_The_Upload()
    {
        using var cancellation = new CancellationTokenSource();
        var track = SyncTestData.Track();
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))))
        {
            StallAtEnd = true
        };
        var files = new AfcPeer
        {
            AllowWrites = true,
            BeforeFileWriteReplyAsync = async token =>
            {
                cancellation.Cancel();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
        };
        await using var afc = new AfcClient(files, Timeout);
        await using var client = new AtcClient(peer, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        var exception = Assert.CatchAsync<OperationCanceledException>(() => session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            cancellation.Token));
        Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(peer.WasDisposed, Is.True);
        Assert.That(files.WasDisposed, Is.True);
    }

    [Test]
    public async Task Rejects_SyncFinished_Before_The_Requested_Audio_Has_Arrived()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))),
            SyncTestData.Message(AtcCommands.SyncFinished));
        var files = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(files, Timeout);
        await using var client = new AtcClient(peer, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        var exception = Assert.ThrowsAsync<IOException>(() => session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("before every requested asset"));
        Assert.That(files.WritePackets.Count, Is.Zero);
    }

    [TestCase(AtcCommands.SyncFailed)]
    [TestCase(AtcCommands.SyncAllowed)]
    public async Task Answers_Pings_And_Cancels_A_Blocked_Upload_When_Sync_Is_Revoked(string command)
    {
        var track = SyncTestData.Track();
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pongSent = false;
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))),
            SyncTestData.Message(AtcCommands.Ping),
            SyncTestData.Message(command))
        {
            BeforeFrameAsync = (frame, token) => frame >= 4 ? uploadStarted.Task.WaitAsync(token) : Task.CompletedTask,
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) == AtcCommands.Pong) pongSent = true;
            }
        };
        var files = new AfcPeer
        {
            AllowWrites = true,
            BeforeFileWriteReplyAsync = async token =>
            {
                uploadStarted.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
        };
        await using var afc = new AfcClient(files, Timeout);
        await using var client = new AtcClient(peer, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        var exception = Assert.ThrowsAsync<IOException>(() => session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain(command == AtcCommands.SyncFailed ? command : "revoked"));
        Assert.That(pongSent, Is.True);
        Assert.That(files.WritePackets.Count, Is.EqualTo(1));
        Assert.That(files.WasDisposed, Is.True);
        Assert.That(peer.SentMessages().Any(message => message.Text(AtcKeys.Command) == AtcCommands.FileComplete),
            Is.False);
    }

    [Test]
    public async Task Allows_Atc_Silence_During_Upload_And_Applies_The_Timeout_Afterward()
    {
        var track = SyncTestData.Track();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))),
            SyncTestData.Message(AtcCommands.SyncFinished))
        {
            BeforeFrameAsync = (frame, token) => frame >= 4 ? completed.Task.WaitAsync(token) : Task.CompletedTask,
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) == AtcCommands.FileComplete) completed.TrySetResult();
            }
        };
        var files = new AfcPeer
        {
            AllowWrites = true,
            BeforeFileWriteReplyAsync = token => Task.Delay(TimeSpan.FromMilliseconds(300), token)
        };
        await using var afc = new AfcClient(files, TimeSpan.FromSeconds(3));
        await using var client = new AtcClient(peer, TimeSpan.FromMilliseconds(100));
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        await session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None);
        Assert.That(completed.Task.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task Times_Out_After_The_Last_Asset_Without_A_Terminal_Response()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))))
        {
            StallAtEnd = true
        };
        await using var afc = new AfcClient(new AfcPeer { AllowWrites = true }, Timeout);
        await using var client = new AtcClient(peer, TimeSpan.FromMilliseconds(100));
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        var exception = Assert.ThrowsAsync<IOException>(() => session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("Timed out"));
        Assert.That(peer.WasDisposed, Is.True);
    }

    [TestCase(AtcCommands.SyncFailed)]
    [TestCase(AtcCommands.FileError)]
    [TestCase(AtcCommands.SyncAllowed)]
    public async Task Stops_Transferring_When_The_Control_Channel_Reports_A_Failure(string command)
    {
        var first = SyncTestData.Track();
        var second = first with { Id = first.Id - 1 };
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(
                SyncTestData.Asset(first.AssetId), SyncTestData.Asset(second.AssetId))),
            SyncTestData.Message(command));
        var files = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(files, Timeout);
        await using var client = new AtcClient(peer, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        Assert.ThrowsAsync<IOException>(() => session.TransferAsync(
            afc,
            [first, second],
            new HashSet<long>(),
            CancellationToken.None));
        Assert.That(files.WritePackets.Count, Is.Zero);
        Assert.That(peer.SentMessages().Any(message => message.Text(AtcKeys.Command) == AtcCommands.FileComplete),
            Is.False);
    }

    [Test]
    public async Task Transfers_And_Reads_Back_Audio_Then_Answers_Pings_Until_Current_Session_Finishes()
    {
        var track = SyncTestData.Track();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))),
            SyncTestData.Message(AtcCommands.SyncFinished, session: 1),
            SyncTestData.Message(AtcCommands.Ping),
            SyncTestData.Message(AtcCommands.SyncFinished))
        {
            BeforeFrameAsync = (frame, token) => frame >= 4 ? completed.Task.WaitAsync(token) : Task.CompletedTask,
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) == AtcCommands.FileComplete) completed.TrySetResult();
            }
        };
        var files = new AfcPeer { AllowWrites = true };
        await using var afc = new AfcClient(files, Timeout);
        await using var client = new AtcClient(peer, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        await session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None);
        Assert.That(files.Files[SyncPaths.Airlock + track.AssetId], Is.EqualTo(await File.ReadAllBytesAsync(track.LocalPath)));
        Assert.That(files.OpenHandles, Is.Zero);
        var messages = peer.SentMessages();
        Assert.That(messages.Select(message => message.Text(AtcKeys.Command)), Does.Contain(AtcCommands.Pong));
        var fileComplete = messages.Single(message => message.Text(AtcKeys.Command) == AtcCommands.FileComplete);
        Assert.That(fileComplete[AtcKeys.Parameters].AsDict().Text(AtcKeys.AssetPath), Is.EqualTo(track.DevicePath));
        var finished = messages.Single(message => message.Text(AtcKeys.Command) == AtcCommands.FinishedMetadata);
        Assert.That(finished[AtcKeys.Parameters].AsDict()[AtcKeys.Anchors].AsDict().Text(AtcKeys.Media), Is.EqualTo("8"));
    }

    [Test]
    public async Task Rejects_An_Anchor_Change_Before_Metadata_Or_Assets_Are_Sent()
    {
        var peer = new AtcPeer(SyncTestData.Capabilities(), SyncTestData.Allowed(), SyncTestData.Ready("8"));
        await using var client = new AtcClient(peer, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        Assert.ThrowsAsync<IOException>(() => session.BeginAsync(Identity, 7, CancellationToken.None));
        Assert.That(peer.SentMessages().Select(message => message.Text(AtcKeys.Command)),
            Is.EqualTo([AtcCommands.Ping, AtcCommands.RequestingSync]));
    }

    [Test]
    public async Task Detects_Corrupt_Transfers_Before_FileComplete()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))))
        {
            StallAtEnd = true
        };
        await using var client = new AtcClient(peer, Timeout);
        await using var afc = new AfcClient(new AfcPeer { AllowWrites = true, CorruptWrite = true }, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        Assert.ThrowsAsync<IOException>(() => session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None));
        Assert.That(peer.SentMessages().Any(message => message.Text(AtcKeys.Command) == AtcCommands.FileComplete), Is.False);
    }

    [Test]
    public async Task Accepts_A_Metadata_Only_Delete_Without_An_Asset_Manifest()
    {
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.Progress),
            SyncTestData.Message(AtcCommands.SyncFinished));
        await using var client = new AtcClient(peer, Timeout);
        await using var afc = new AfcClient(new AfcPeer(), Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        await session.TransferAsync(
            afc,
            [],
            new HashSet<long>(),
            CancellationToken.None);
    }

    [Test]
    public async Task Rejects_An_Entire_Manifest_Before_Any_File_Is_Transferred()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(
                SyncTestData.Asset(track.AssetId), SyncTestData.Asset("999999"))));
        var files = new AfcPeer { AllowWrites = true };
        await using var client = new AtcClient(peer, Timeout);
        await using var afc = new AfcClient(files, Timeout);
        await client.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(client, TextWriter.Null);
        using var signer = await session.BeginAsync(Identity, 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        Assert.ThrowsAsync<InvalidDataException>(() => session.TransferAsync(
            afc,
            [track],
            new HashSet<long>(),
            CancellationToken.None));
        Assert.That(files.Opens, Is.Zero);
    }
}
