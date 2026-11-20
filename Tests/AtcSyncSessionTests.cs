using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class AtcSyncSessionTests
{
    [Test]
    public async Task Reports_Transfer_Completion_Only_After_Successful_Readback([Values] bool corrupt)
    {
        var track = SyncTestData.Track();
        var peer = CreateCompletingAudioPeer(track, out _);
        await using var error = new StringWriter();
        var files = new AfcPeer { AllowWrites = true, CorruptWrite = corrupt };
        await using var sync = new AtcSyncTestSession(peer, files, error: error);
        await sync.StartAsync();
        if (corrupt)
        {
            await Assert.ThrowsAsync<IOException>(() => sync.TransferAsync([track]));
        }
        else
        {
            await sync.TransferAsync([track]);
        }

        var diagnostics = error.ToString();
        Assert.That(diagnostics, Does.Contain("Transferring assets: 0% (0 B / "));
        Assert.That(diagnostics.Contains("Transferring assets: 100%", StringComparison.Ordinal), Is.EqualTo(!corrupt));
        Assert.That(diagnostics, Does.Not.Contain("Transfer: "));
    }

    [Test]
    public async Task Transfers_Audio_Without_Cover_With_Or_Without_Existing_Airlock([Values] bool airlockExists)
    {
        var track = SyncTestData.Track();
        Assert.That(track.Metadata.Artwork, Is.Null);
        var peer = CreateCompletingAudioPeer(track, out _);
        var files = new AfcPeer { AllowWrites = true, RequireParentDirectories = true };
        if (airlockExists)
        {
            files.Directories.Add(SyncPaths.Airlock.TrimEnd('/'));
        }

        await using var sync = new AtcSyncTestSession(peer, files);
        await sync.StartAsync();
        await sync.TransferAsync([track]);

        Assert.That(files.Directories, Does.Contain(SyncPaths.Airlock.TrimEnd('/')));
        Assert.That(files.Files[SyncPaths.Airlock + track.AssetId], Is.EqualTo(await File.ReadAllBytesAsync(track.LocalPath)));
    }

    [Test]
    public async Task Caller_Cancellation_Stops_The_Control_Reader_And_The_Upload()
    {
        using var cancellation = new CancellationTokenSource();
        var track = SyncTestData.Track();
        var peer = CreatePeer(track);

        var files = new AfcPeer
        {
            AllowWrites = true,
            BeforeFileWriteReplyAsync = async token =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        await using var sync = new AtcSyncTestSession(peer, files);
        await sync.StartAsync();
        var exception = await Assert.CatchAsync<OperationCanceledException>(() => sync.TransferAsync([track], cancellation.Token));

        Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(peer.WasDisposed, Is.True);
        Assert.That(files.WasDisposed, Is.True);
    }

    [Test]
    public async Task Rejects_SyncFinished_Before_The_Requested_Audio_Has_Arrived()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            AudioManifestMessage(track.AssetId),
            SyncTestData.Message(AtcCommands.SyncFinished)));

        var files = new AfcPeer { AllowWrites = true };
        await using var sync = new AtcSyncTestSession(peer, files);
        await AssertTransferFailureAsync(sync, track, "before every requested asset");
        Assert.That(files.WritePackets.Count, Is.Zero);
    }

    [TestCase(AtcCommands.SyncFailed)]
    [TestCase(AtcCommands.SyncAllowed)]
    public async Task Answers_Pings_And_Cancels_A_Blocked_Upload_When_Sync_Is_Revoked(string command)
    {
        const int firstControlFrameIndex = 4;
        var track = SyncTestData.Track();
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pongSent = false;
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            AudioManifestMessage(track.AssetId),
            SyncTestData.Message(AtcCommands.Ping),
            SyncTestData.Message(command)))
        {
            BeforeFrameAsync = (frame, token) => frame >= firstControlFrameIndex
                ? uploadStarted.Task.WaitAsync(token)
                : Task.CompletedTask,
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
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        await using var sync = new AtcSyncTestSession(peer, files);
        await sync.StartAsync();
        var exception = await Assert.ThrowsAsync<IOException>(() => sync.TransferAsync([track]));

        Assert.That(exception!.Message, Does.Contain(command == AtcCommands.SyncFailed ? command : "revoked"));
        Assert.That(pongSent, Is.True);
        Assert.That(files.WritePackets.Count, Is.EqualTo(1));
        Assert.That(files.WasDisposed, Is.True);
        AssertNoCompletedAssets(peer);
    }

    [Test]
    public async Task Allows_Atc_Silence_During_Upload_And_Applies_The_Timeout_Afterward()
    {
        var track = SyncTestData.Track();
        var peer = CreateCompletingAudioPeer(track, out var completed);
        var files = new AfcPeer
        {
            AllowWrites = true,
            BeforeFileWriteReplyAsync = token => Task.Delay(TimeSpan.FromMilliseconds(300), token)
        };

        await using var sync = new AtcSyncTestSession(
            peer,
            files,
            atcTimeout: TimeSpan.FromMilliseconds(100),
            afcTimeout: TimeSpan.FromSeconds(3));

        await sync.StartAsync();
        await sync.TransferAsync([track]);

        Assert.That(completed.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task Times_Out_After_The_Last_Asset_Without_A_Terminal_Response()
    {
        var track = SyncTestData.Track();
        var peer = CreatePeer(track);

        await using var sync = new AtcSyncTestSession(peer, atcTimeout: TimeSpan.FromMilliseconds(100));
        await AssertTransferFailureAsync(sync, track, "Timed out");
        Assert.That(peer.WasDisposed, Is.True);
    }

    [TestCase(AtcCommands.SyncFailed)]
    [TestCase(AtcCommands.FileError)]
    [TestCase(AtcCommands.SyncAllowed)]
    public async Task Stops_Transferring_When_The_Control_Channel_Reports_A_Failure(string command)
    {
        var first = SyncTestData.Track();
        var second = first with { Id = first.Id - 1 };
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            AudioManifestMessage(first.AssetId, second.AssetId),
            SyncTestData.Message(command)));

        var files = new AfcPeer { AllowWrites = true };
        await using var sync = new AtcSyncTestSession(peer, files);
        await sync.StartAsync();
        await Assert.ThrowsAsync<IOException>(() => sync.TransferAsync([first, second]));

        Assert.That(files.WritePackets.Count, Is.Zero);
        AssertNoCompletedAssets(peer);
    }

    [Test]
    public async Task Transfers_And_Reads_Back_Audio_Then_Answers_Pings_Until_Current_Session_Finishes()
    {
        var track = SyncTestData.Track();
        var peer = CreateCompletingAudioPeer(
            track,
            out _,
            SyncTestData.Message(AtcCommands.SyncFinished, session: 1),
            SyncTestData.Message(AtcCommands.Ping));

        var files = new AfcPeer { AllowWrites = true };
        await using var sync = new AtcSyncTestSession(peer, files);
        await sync.StartAsync();
        await sync.TransferAsync([track]);

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
        await using var sync = new AtcSyncTestSession(peer);
        await Assert.ThrowsAsync<IOException>(sync.BeginAsync);
        Assert.That(
            peer.SentMessages().Select(message => message.Text(AtcKeys.Command)),
            Is.EqualTo([AtcCommands.Ping, AtcCommands.RequestingSync]));
    }

    [TestCase("0")]
    [TestCase("2")]
    [TestCase(1L)]
    [TestCase(true)]
    public async Task Rejects_Unsupported_Live_VoiceMemo_Anchors_Before_Metadata_Is_Announced(object anchor)
    {
        var ready = SyncTestData.Ready();
        ready.Dictionary(AtcKeys.Parameters).Dictionary(AtcKeys.Anchors)[AtcKeys.VoiceMemo] = anchor;
        var peer = new AtcPeer(SyncTestData.Capabilities(), SyncTestData.Allowed(), ready);
        await using var sync = new AtcSyncTestSession(peer);

        var exception = await Assert.ThrowsAsync<IOException>(sync.BeginAsync);
        Assert.That(exception!.Message, Does.Contain("VoiceMemo"));
        Assert.That(
            peer.SentMessages().Select(message => message.Text(AtcKeys.Command)),
            Is.EqualTo([AtcCommands.Ping, AtcCommands.RequestingSync]));
    }

    [TestCase(null)]
    [TestCase(SyncProtocol.VoiceMemoAnchor)]
    public async Task Allows_Missing_Or_Qualified_Live_VoiceMemo_Anchors(string? anchor)
    {
        var ready = SyncTestData.Ready();
        if (anchor is not null)
        {
            ready.Dictionary(AtcKeys.Parameters).Dictionary(AtcKeys.Anchors)[AtcKeys.VoiceMemo] = anchor;
        }

        var peer = new AtcPeer(SyncTestData.Capabilities(), SyncTestData.Allowed(), ready);
        await using var sync = new AtcSyncTestSession(peer);
        await sync.StartAsync();
        var sent = peer.SentMessages().Last().Dictionary(AtcKeys.Parameters).Dictionary(AtcKeys.Anchors);
        Assert.That(sent.Text(AtcKeys.VoiceMemo), Is.EqualTo(SyncProtocol.VoiceMemoAnchor));
    }

    [Test]
    public async Task Detects_Corrupt_Transfers_Before_FileComplete()
    {
        var track = SyncTestData.Track();
        var peer = CreatePeer(track);

        await using var sync = new AtcSyncTestSession(peer, new AfcPeer { AllowWrites = true, CorruptWrite = true });
        await sync.StartAsync();
        await Assert.ThrowsAsync<IOException>(() => sync.TransferAsync([track]));

        AssertNoCompletedAssets(peer);
    }

    [Test]
    public async Task Accepts_A_Metadata_Only_Delete_Without_An_Asset_Manifest()
    {
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            SyncTestData.Message(AtcCommands.Progress),
            SyncTestData.Message(AtcCommands.SyncFinished)));

        await using var sync = new AtcSyncTestSession(peer, new AfcPeer());
        await sync.StartAsync();
        await sync.TransferAsync([]);
    }

    [Test]
    public async Task Rejects_An_Entire_Manifest_Before_Any_File_Is_Transferred()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            AudioManifestMessage(track.AssetId, "999999")));

        var files = new AfcPeer { AllowWrites = true };
        await using var sync = new AtcSyncTestSession(peer, files);
        await sync.StartAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.TransferAsync([track]));

        Assert.That(files.Opens, Is.Zero);
    }

    private static void AssertNoCompletedAssets(AtcPeer peer)
    {
        Assert.That(peer.SentMessages().Any(message => message.Text(AtcKeys.Command) == AtcCommands.FileComplete), Is.False);
    }

    private static async Task AssertTransferFailureAsync(
        AtcSyncTestSession sync,
        PreparedTrack track,
        string expectedMessage)
    {
        await sync.StartAsync();
        var exception = await Assert.ThrowsAsync<IOException>(() => sync.TransferAsync([track]));
        Assert.That(exception!.Message, Does.Contain(expectedMessage));
    }

    private static AtcPeer CreatePeer(PreparedTrack track)
    {
        return new AtcPeer(SyncTestData.SessionMessages(AudioManifestMessage(track.AssetId))) { StallAtEnd = true };
    }

    private static Dictionary<string, object> AudioManifestMessage(params string[] assetIds)
    {
        return SyncTestData.Message(
            AtcCommands.AssetManifest,
            SyncTestData.Manifest([.. assetIds.Select(id => SyncTestData.Asset(id))]));
    }

    private static AtcPeer CreateCompletingAudioPeer(
        PreparedTrack track,
        out Task completed,
        params Dictionary<string, object>[] responses)
    {
        const int terminalFrameIndex = 4;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed = completion.Task;
        return new AtcPeer(SyncTestData.SessionMessages(
            [AudioManifestMessage(track.AssetId), .. responses, SyncTestData.Message(AtcCommands.SyncFinished)]))
        {
            BeforeFrameAsync = (frame, token) => frame >= terminalFrameIndex
                ? completion.Task.WaitAsync(token)
                : Task.CompletedTask,
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) == AtcCommands.FileComplete) completion.TrySetResult();
            }
        };
    }
}
