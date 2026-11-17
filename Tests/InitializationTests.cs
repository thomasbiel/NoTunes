using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class InitializationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [TestCase(false, SyncPaths.Metadata)]
    [TestCase(false, SyncPaths.Metadata + SyncPaths.SignatureSuffix)]
    [TestCase(false, SyncPaths.MetadataDirectory + "/Sync_00000001.plist")]
    [TestCase(true, SyncPaths.Metadata)]
    [TestCase(true, SyncPaths.Metadata + SyncPaths.SignatureSuffix)]
    [TestCase(true, SyncPaths.MetadataDirectory + "/Sync_00000001.plist")]
    public async Task Preserves_Foreign_Staging_Before_The_Receiver_Prepares_A_Session(bool recovering, string foreignPath)
    {
        using var state = new SyncWorkspace();
        using var fixture = Fresh();
        var saved = await SaveStateAsync(fixture, state.Root);
        if (!recovering)
        {
            File.Delete(Path.Combine(DeviceState.DirectoryFor(state.Root, "phone"), "initialization.json"));
        }

        var foreign = new byte[] { 90, 91, 92 };
        var files = new AfcPeer { AllowWrites = true };
        files.Directories.Add(SyncPaths.MetadataDirectory);
        files.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        if (recovering)
        {
            files.Files[SyncPaths.Metadata] = saved.Operation;
            files.Files[SyncPaths.Metadata + SyncPaths.SignatureSuffix] = saved.Signature!;
        }

        files.Files[foreignPath] = foreign;
        var started = false;
        var peer = Peer(files, fixture, () =>
        {
            started = true;
            // Receiver prepare deletes every Sync_*.plist, including files from another operation.
            foreach (var path in files.Files.Keys.Where(path =>
                path.StartsWith(SyncPaths.MetadataDirectory + "/Sync_", StringComparison.Ordinal)
                && path.EndsWith(".plist", StringComparison.Ordinal)).ToArray())
            {
                files.Files.Remove(path);
            }
        });
        await using var afc = new AfcClient(files, Timeout);
        await using var atc = new AtcClient(peer, Timeout);
        await atc.ReadSyncStatusAsync(CancellationToken.None);
        var initializer = new LibraryInitializer(new SilentUsbMuxTransport(), TextWriter.Null);
        await Assert.ThrowsAsync<IOException>(() => initializer.InitializeAsync(
            afc,
            atc,
            "Test iPhone",
            "phone",
            new InitOptions { StateFolder = state.Root },
            CancellationToken.None));
        Assert.That(started, Is.False);
        Assert.That(files.Files[foreignPath], Is.EqualTo(foreign));
    }

    [Test]
    public async Task Removes_Own_Metadata_Prefixes_Before_Starting_A_Recovery_Session()
    {
        using var state = new SyncWorkspace();
        using var fixture = Fresh();
        var saved = await SaveStateAsync(fixture, state.Root);
        var files = new AfcPeer { AllowWrites = true };
        files.Directories.Add(SyncPaths.MetadataDirectory);
        files.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        files.Files[SyncPaths.Metadata] = saved.Operation[..^1];
        files.Files[SyncPaths.Metadata + SyncPaths.SignatureSuffix] = saved.Signature![..^1];
        var peer = Peer(files, fixture, () =>
        {
            Assert.That(files.Files.ContainsKey(SyncPaths.Metadata), Is.False);
            Assert.That(files.Files.ContainsKey(SyncPaths.Metadata + SyncPaths.SignatureSuffix), Is.False);
        });
        await using var afc = new AfcClient(files, Timeout);
        await using var atc = new AtcClient(peer, Timeout);
        await atc.ReadSyncStatusAsync(CancellationToken.None);
        var initializer = new LibraryInitializer(new SilentUsbMuxTransport(), TextWriter.Null);
        var result = await initializer.InitializeAsync(
            afc,
            atc,
            "Test iPhone",
            "phone",
            new InitOptions { StateFolder = state.Root },
            CancellationToken.None);
        Assert.That(result.DatabaseId, Is.EqualTo(saved.DatabaseId));
    }

    [TestCase(0, false)]
    [TestCase(0, true)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(7, true)]
    public async Task Reports_An_Existing_Initialization_Only_After_The_First_Revision_And_Primary_Container_Exist(
        int revision,
        bool hasPrimaryContainer)
    {
        using var state = new SyncWorkspace();
        using var fixture = Fresh();
        var before = await CatalogueReader.ReadAsync(
            fixture.Snapshot(), CancellationToken.None, allowUninitialized: true);
        var saved = new InitializationState(
            LibraryPreferences.Create(),
            2001,
            2002,
            SyncOperations.Initialize(2001, 2002, "Test iPhone"),
            CatalogueCheckpoint.Capture(before));
        DeviceState.Save(Path.Combine(DeviceState.DirectoryFor(state.Root, "phone"), "initialization.json"), saved);
        fixture.Execute($"INSERT INTO db_info VALUES({saved.DatabaseId},{saved.ContainerId}); "
            + $"INSERT INTO _MLDatabaseProperties VALUES('MLSyncClientSyncAnchor','{revision}');");
        if (hasPrimaryContainer)
        {
            fixture.Execute($"INSERT INTO container(sync_id,container_pid) "
                + $"VALUES({saved.ContainerId},{saved.ContainerId});");
        }

        var files = new AfcPeer();
        files.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        files.Files[SyncPaths.Preferences] = saved.Preferences;
        files.Files[SyncPaths.PreferencesPlist] = LibraryPreferences.Wrap(saved.Preferences);
        await using var afc = new AfcClient(files, Timeout);
        using var peer = new AtcPeer();
        await using var atc = new AtcClient(peer, Timeout);
        var initializer = new LibraryInitializer(new SilentUsbMuxTransport(), TextWriter.Null);
        var options = new InitOptions { StateFolder = state.Root };
        if (revision == 0 || !hasPrimaryContainer)
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => initializer.InitializeAsync(
                afc,
                atc,
                "Test iPhone",
                "phone",
                options,
                CancellationToken.None));
            Assert.That(exception!.Message, Does.Contain("incomplete"));
        }
        else
        {
            var result = await initializer.InitializeAsync(
                afc,
                atc,
                "Test iPhone",
                "phone",
                options,
                CancellationToken.None);
            Assert.That(result.AlreadyInitialized, Is.True);
            Assert.That(result.DatabaseId, Is.EqualTo(saved.DatabaseId));
        }

        Assert.That(files.WritePackets, Is.Empty);
        Assert.That(files.Files.ContainsKey(SyncPaths.Lock), Is.False);
        Assert.That(peer.SentMessages(), Is.Empty);
    }

    [Test]
    public void Generates_Compatible_Independent_Library_Preferences()
    {
        var raw = LibraryPreferences.Create();
        Assert.That(raw, Has.Length.EqualTo(1232));
        Assert.That(LibraryIdentity.Parse(raw), Is.EqualTo(LibraryIdentity.Parse(LibraryPreferences.Wrap(raw))));
        Assert.That(LibraryIdentity.Parse(raw), Is.Not.EqualTo(LibraryIdentity.Parse(LibraryPreferences.Create())));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Initializes_Only_A_New_Library_And_Verifies_The_Committed_Result(bool dryRun)
    {
        using var state = new SyncWorkspace();
        using var fixture = Fresh();
        var files = new AfcPeer { AllowWrites = !dryRun };
        files.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        var peer = Peer(files, fixture);
        await using var afc = new AfcClient(files, Timeout);
        await using var atc = new AtcClient(peer, Timeout);
        await atc.ReadSyncStatusAsync(CancellationToken.None);
        var initializer = new LibraryInitializer(new SilentUsbMuxTransport(), TextWriter.Null);
        var options = new InitOptions { StateFolder = state.Root, DryRun = dryRun };
        var result = await initializer.InitializeAsync(
            afc,
            atc,
            "Test iPhone",
            "phone",
            options,
            CancellationToken.None);
        Assert.That(result.DryRun, Is.EqualTo(dryRun));
        Assert.That(files.Files.ContainsKey(SyncPaths.Preferences), Is.EqualTo(!dryRun));
        if (dryRun)
        {
            Assert.That(files.WritePackets, Is.Empty);
            Assert.That(peer.SentMessages().Select(message => message.Text(AtcKeys.Command)), Is.EqualTo([AtcCommands.Ping]));
            return;
        }

        var catalogue = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(catalogue.DatabaseId, Is.EqualTo(result.DatabaseId));
        Assert.That(catalogue.Anchor, Is.EqualTo(1));
        Assert.That(catalogue.Tracks, Is.Empty);
        Assert.That(files.Files[SyncPaths.PreferencesPlist], Is.EqualTo(LibraryPreferences.Wrap(files.Files[SyncPaths.Preferences])));
    }

    [Test]
    public async Task Resumes_An_Interrupted_Preference_Write_With_The_Same_Identity()
    {
        using var state = new SyncWorkspace();
        using var fixture = Fresh();
        var failed = new AfcPeer { AllowWrites = true, FailWrite = true };
        failed.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        var initializer = new LibraryInitializer(new SilentUsbMuxTransport(), TextWriter.Null);
        var options = new InitOptions { StateFolder = state.Root };
        await using (var afc = new AfcClient(failed, Timeout))
        await using (var atc = new AtcClient(Peer(failed, fixture), Timeout))
        {
            await atc.ReadSyncStatusAsync(CancellationToken.None);
            await Assert.ThrowsAsync<AfcException>(() => initializer.InitializeAsync(
                afc,
                atc,
                "Test iPhone",
                "phone",
                options,
                CancellationToken.None));
        }

        var record = Directory.GetFiles(state.Root, "initialization.json", SearchOption.AllDirectories).Single();
        var saved = System.Text.Json.JsonSerializer.Deserialize<InitializationState>(File.ReadAllBytes(record))!;
        var retry = new AfcPeer { AllowWrites = true };
        foreach (var file in failed.Files)
        {
            retry.Files[file.Key] = file.Value;
        }

        await using var retryAfc = new AfcClient(retry, Timeout);
        await using var retryAtc = new AtcClient(Peer(retry, fixture), Timeout);
        await retryAtc.ReadSyncStatusAsync(CancellationToken.None);
        var result = await initializer.InitializeAsync(
            retryAfc,
            retryAtc,
            "Test iPhone",
            "phone",
            options,
            CancellationToken.None);
        Assert.That(result.DatabaseId, Is.EqualTo(saved.DatabaseId));
        Assert.That(retry.Files[SyncPaths.Preferences], Is.EqualTo(saved.Preferences));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Refuses_Existing_Libraries_And_Foreign_Preferences_Without_Writing(bool preferencesOnly)
    {
        using var state = new SyncWorkspace();
        using var fixture = preferencesOnly ? Fresh() : new CatalogueFixture();
        var files = new AfcPeer();
        files.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
        if (preferencesOnly) files.Files[SyncPaths.Preferences] = LibraryPreferences.Create();

        await using var afc = new AfcClient(files, Timeout);
        await using var atc = new AtcClient(Peer(files, fixture), Timeout);
        await atc.ReadSyncStatusAsync(CancellationToken.None);
        var initializer = new LibraryInitializer(new SilentUsbMuxTransport(), TextWriter.Null);
        await Assert.ThrowsAsync<IOException>(() => initializer.InitializeAsync(
            afc,
            atc,
            "Test iPhone",
            "phone",
            new InitOptions { StateFolder = state.Root },
            CancellationToken.None));
        Assert.That(files.WritePackets, Is.Empty);
        Assert.That(files.Files.ContainsKey(SyncPaths.Lock), Is.False);
    }

    private static CatalogueFixture Fresh()
    {
        var fixture = new CatalogueFixture();
        fixture.Execute("""
            DELETE FROM db_info; DELETE FROM item; DELETE FROM item_store; DELETE FROM item_extra;
            DELETE FROM container; DELETE FROM _MLDatabaseProperties;
            """);
        return fixture;
    }

    private static async Task<InitializationState> SaveStateAsync(CatalogueFixture fixture, string root)
    {
        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None, allowUninitialized: true);
        var saved = new InitializationState(
            LibraryPreferences.Create(),
            2001,
            2002,
            SyncOperations.Initialize(2001, 2002, "Test iPhone"),
            CatalogueCheckpoint.Capture(before),
            new byte[] { 1, 2, 3 });
        DeviceState.Save(Path.Combine(DeviceState.DirectoryFor(root, "phone"), "initialization.json"), saved);
        return saved;
    }

    private static AtcPeer Peer(AfcPeer files, CatalogueFixture fixture, Action? onRequestingSync = null)
    {
        return new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready("0"),
            SyncTestData.Message(AtcCommands.SyncFinished))
        {
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) == AtcCommands.RequestingSync)
                {
                    onRequestingSync?.Invoke();
                }

                if (message.Text(AtcKeys.Command) != AtcCommands.FinishedMetadata) return;

                var document = Plist.Parse(files.Files[SyncPaths.Metadata]).AsDict();
                var operations = (object[])document[MediaKeys.Operations];
                Assert.That(operations, Has.Length.EqualTo(2));
                var database = operations[0].AsDict().Number(MediaKeys.PersistentId);
                var playlist = operations[1].AsDict();
                Assert.That(playlist.Text(MediaKeys.Operation), Is.EqualTo("insert_playlist"));
                Assert.That(playlist.Dictionary("container").Flag("is_hidden"), Is.True);
                var container = playlist.Number(MediaKeys.PersistentId);
                fixture.Execute($"INSERT INTO db_info VALUES({database},{container}); "
                    + $"INSERT INTO container(sync_id,container_pid) VALUES({container},{container}); "
                    + "INSERT INTO _MLDatabaseProperties VALUES('MLSyncClientSyncAnchor','1');");
                files.Files[CatalogueSnapshot.DatabasePath] = fixture.Snapshot().Database;
                files.Files.Remove(SyncPaths.Metadata);
                files.Files.Remove(SyncPaths.Metadata + SyncPaths.SignatureSuffix);
            }
        };
    }
}
