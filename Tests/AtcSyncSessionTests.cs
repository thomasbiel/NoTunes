using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class AtcSyncSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly LibraryIdentity Identity = new("0011223344556677", "binding");

    [Test]
    public async Task Transfers_And_Reads_Back_Audio_Then_Answers_Pings_Until_Current_Session_Finishes()
    {
        var track = SyncTestData.Track();
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))),
            SyncTestData.Message(AtcCommands.SyncFinished, session: 1),
            SyncTestData.Message(AtcCommands.Ping),
            SyncTestData.Message(AtcCommands.SyncFinished));
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
        var completed = messages.Single(message => message.Text(AtcKeys.Command) == AtcCommands.FileComplete);
        Assert.That(completed[AtcKeys.Parameters].AsDict().Text(AtcKeys.AssetPath), Is.EqualTo(track.DevicePath));
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
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId))));
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
