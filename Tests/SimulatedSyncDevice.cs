using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Music;
using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class SimulatedSyncDevice : IDisposable
{
    internal const string Udid = "integration-phone";
    private const uint DeviceId = 1;
    private const long ProductId = 1;
    private const string UsbConnectionType = "USB";
    private const string InjectedFailure = "Injected sync interruption.";
    private const int RequestTimeoutSeconds = 5;
    private readonly SimulatedSyncCatalogue catalogue;
    private readonly HashSet<string> directories = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> audioPaths = [];

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<Dictionary<string, object>[]> Batches { get; } = [];

    public List<SimulatedSyncConnection> Connections { get; } = [];

    public List<AfcPeer> FileConnections { get; } = [];

    public SyncInterruptionPoint Interruption { get; set; }

    public int InterruptAtBatch { get; set; } = 1;

    public int StartedSessions { get; private set; }

    public bool CorruptUploadedAudio { get; init; }

    public Action<AfcPeer>? ConfigureFileConnection { get; init; }

    public GroupNamePrompt? PromptForGroupName { get; init; }

    public uint Anchor => this.catalogue.Anchor;

    public SimulatedSyncDevice(bool keepExistingTrack = false)
    {
        this.catalogue = new SimulatedSyncCatalogue(keepExistingTrack);
        this.Files[SyncPaths.Preferences] = LibraryPreferences.Create();
        this.Files[SyncPaths.PreferencesPlist] = LibraryPreferences.Wrap(this.Files[SyncPaths.Preferences]);
        if (keepExistingTrack)
        {
            this.SetExistingAudio([1, 2, 3, 4, 5]);
        }

        this.RefreshCatalogue();
    }

    public IUsbMuxTransport CreateTransport()
    {
        var reply = Plist.Dict((UsbMuxKeys.DeviceList, new object[]
        {
            Plist.Dict(
                (UsbMuxKeys.DeviceId, (long)DeviceId),
                (UsbMuxKeys.Properties, Plist.Dict(
                    (UsbMuxKeys.ConnectionType, UsbConnectionType),
                    (UsbMuxKeys.SerialNumber, Udid),
                    (UsbMuxKeys.ProductId, ProductId))))
        }));
        return new ReplyUsbMuxTransport(new ScriptedStream(ProtocolFrames.UsbMux(1, reply)));
    }

    public async Task<SyncResult> RunAsync(
        string source,
        string state,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null)
            .BuildServiceProvider();
        var engine = new SyncEngine(
            services.GetRequiredService<SourceScanner>(),
            services.GetRequiredService<ISourceFileSystem>(),
            this.CreateTransport(),
            TextWriter.Null)
        {
            OpenConnection = this.OpenAsync,
            PromptForGroupName = this.PromptForGroupName
        };
        var options = new SyncOptions
        {
            Folder = source,
            StateFolder = state,
            Timeout = RequestTimeoutSeconds,
            DryRun = dryRun
        };
        return await engine.RunAsync(options, cancellationToken);
    }

    public async Task<IDeviceSyncConnection> OpenAsync(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Assert.That(device.Udid, Is.EqualTo(Udid));
        var assetsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assets = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        var terminalFrame = int.MaxValue;
        AtcPeer? peer = null;
        peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(this.Anchor.ToString(CultureInfo.InvariantCulture)))
        {
            BeforeFrameAsync = (frame, token) => frame == terminalFrame
                ? assetsFinished.Task.WaitAsync(token) : Task.CompletedTask,
            OnMessageSent = message =>
            {
                switch (message.Text(AtcKeys.Command))
                {
                    case AtcCommands.RequestingSync:
                        this.StartedSessions++;
                        break;
                    case AtcCommands.FinishedMetadata:
                        var document = Plist.Parse(this.Files[SyncPaths.Metadata]).AsDict();
                        using (var signer = new GrappaSigner(GrappaSignerTests.Reply()))
                        {
                            Assert.That(this.Files[SyncPaths.Metadata + SyncPaths.SignatureSuffix],
                                Is.EqualTo(signer.Sign(this.Files[SyncPaths.Metadata])));
                        }

                        var changes = ((object[])document[MediaKeys.Operations]).Select(value => value.AsDict())
                            .Where(operation => operation.Text(MediaKeys.Operation) != MediaOperations.UpdateDatabase)
                            .ToArray();
                        this.Batches.Add(changes);
                        foreach (var operation in changes)
                        {
                            var id = operation.Number(MediaKeys.PersistentId);
                            if (operation.Text(MediaKeys.Operation) == MediaOperations.DeleteTrack)
                            {
                                if (this.audioPaths.Remove(id, out var path)) this.Files.Remove(path);
                            }
                            else
                            {
                                assets.Add(id.ToString(CultureInfo.InvariantCulture), operation);
                            }
                        }

                        this.catalogue.Apply(document);
                        this.RefreshCatalogue();
                        this.Interrupt(SyncInterruptionPoint.AfterMetadata);
                        if (assets.Count == 0)
                        {
                            this.ClearMetadata();
                            peer!.Enqueue(SyncTestData.Message(AtcCommands.SyncFinished));
                        }
                        else
                        {
                            var entries = assets.Select(pair => SyncTestData.Asset(pair.Key, AssetParts(pair.Value)))
                                .Cast<object>().ToArray();
                            peer!.Enqueue(
                                SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(entries)),
                                SyncTestData.Message(AtcCommands.SyncFinished));
                            terminalFrame = peer.ResponsesRead + 1;
                        }

                        break;
                    case AtcCommands.FileComplete:
                        var parameters = message.Dictionary(AtcKeys.Parameters);
                        var assetId = parameters.Text(AtcKeys.AssetId);
                        var asset = assets[assetId];
                        this.CompleteAsset(assetId, asset, parameters.Text(AtcKeys.AssetPath));
                        assets.Remove(assetId);
                        this.RefreshCatalogue();
                        if (assets.Count == 0)
                        {
                            this.ClearMetadata();
                            this.Interrupt(SyncInterruptionPoint.AfterAssets);
                            assetsFinished.TrySetResult();
                        }

                        break;
                }
            }
        };
        var files = new AfcPeer
        {
            AllowWrites = true,
            Files = this.Files,
            Directories = this.directories,
            ReadFragmentBytes = SourceFileDefaults.ReadBufferBytes
        };
        this.ConfigureFileConnection?.Invoke(files);
        var connection = new SimulatedSyncConnection(files, peer, timeout);
        this.Connections.Add(connection);
        this.FileConnections.Add(files);
        await connection.Atc.ReadSyncStatusAsync(cancellationToken);
        return connection;
    }

    public void SetExistingAudio(byte[] bytes)
    {
        this.Files[SimulatedSyncCatalogue.ExistingTrackPath] = bytes;
        this.audioPaths[SimulatedSyncCatalogue.ExistingTrackId] = SimulatedSyncCatalogue.ExistingTrackPath;
        this.catalogue.SetExistingAudioSize(bytes.Length);
        this.RefreshCatalogue();
    }

    public Task<DeviceCatalogue> ReadCatalogueAsync()
    {
        return CatalogueReader.ReadAsync(this.catalogue.Snapshot(), CancellationToken.None);
    }

    public void AddAlbumArtist(long rowId, long syncId, string name, string sortName)
    {
        this.catalogue.AddAlbumArtist(
            rowId,
            syncId,
            name,
            sortName);
        this.RefreshCatalogue();
    }

    private void CompleteAsset(string assetId, Dictionary<string, object> operation, string path)
    {
        var id = operation.Number(MediaKeys.PersistentId);
        if (operation.Text(MediaKeys.Operation) == MediaOperations.InsertTrack)
        {
            var staged = SyncPaths.Airlock + assetId;
            var bytes = this.Files[staged];
            this.Files.Remove(staged);
            if (this.CorruptUploadedAudio) bytes[^1] ^= 1;

            this.Files[path] = bytes;
            this.audioPaths[id] = path;
            this.catalogue.CompleteAudio(id, path, bytes.Length);
        }
        else
        {
            Assert.That(path, Is.Empty);
        }

        if (operation.Dictionary(MediaKeys.Item).GetValueOrDefault(MediaKeys.ArtworkCacheId) is long cacheId)
        {
            var staged = SyncPaths.ArtworkAirlock + assetId;
            var relative = this.catalogue.CompleteArtwork(id, cacheId);
            this.Files[SyncPaths.ArtworkOriginals + relative] = this.Files[staged];
            this.Files.Remove(staged);
        }
    }

    private static long AssetParts(Dictionary<string, object> operation)
    {
        var audio = operation.Text(MediaKeys.Operation) == MediaOperations.InsertTrack ? SyncProtocol.AudioPart : 0;
        var artwork = operation.Dictionary(MediaKeys.Item).ContainsKey(MediaKeys.ArtworkCacheId)
            ? SyncProtocol.ArtworkPart : 0;
        return audio | artwork;
    }

    private void Interrupt(SyncInterruptionPoint point)
    {
        if (this.Interruption != point || this.Batches.Count != this.InterruptAtBatch) return;

        this.Interruption = SyncInterruptionPoint.None;
        throw new IOException(InjectedFailure);
    }

    private void RefreshCatalogue()
    {
        this.Files[CatalogueSnapshot.DatabasePath] = this.catalogue.Snapshot().Database;
    }

    private void ClearMetadata()
    {
        this.Files.Remove(SyncPaths.Metadata);
        this.Files.Remove(SyncPaths.Metadata + SyncPaths.SignatureSuffix);
    }

    public void Dispose() => this.catalogue.Dispose();
}
