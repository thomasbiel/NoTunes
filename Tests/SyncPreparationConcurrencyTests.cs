using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncPreparationConcurrencyTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const string FailureMessage = "Injected preparation failure.";
    private SyncWorkspace source = null!;
    private SyncWorkspace state = null!;
    private SyncWorkspace cache = null!;

    [SetUp]
    public void SetUp()
    {
        this.source = new SyncWorkspace();
        this.state = new SyncWorkspace();
        this.cache = new SyncWorkspace();
        SourceTestData.WriteSyncTrack(this.source.Root, "song.mp3");
    }

    [TearDown]
    public void TearDown()
    {
        this.cache.Dispose();
        this.state.Dispose();
        this.source.Dispose();
    }

    [Test]
    public async Task Reads_The_Catalogue_And_Attributes_While_Source_Hashing_Is_Paused([Values] bool dryRun)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        var sourceStarted = NewSignal();
        var attributesRead = NewSignal();
        using var device = new SimulatedSyncDevice(keepExistingTrack: true)
        {
            ConfigureFileConnection = peer =>
            {
                peer.AllowPipelinedFileInfo = true;
                peer.OnFileInfoRead = path =>
                {
                    if (path == SimulatedSyncCatalogue.ExistingTrackPath)
                    {
                        attributesRead.TrySetResult();
                    }
                };
            }
        };

        var files = new BudgetSourceFileSystem
        {
            BeforeRead = token =>
            {
                sourceStarted.TrySetResult();
                attributesRead.Task.WaitAsync(token).GetAwaiter().GetResult();
                Assert.That(device.StartedSessions, Is.Zero, "A write session started before the source was hashed.");
            }
        };

        var engine = this.CreateEngine(device, files, device.OpenAsync);
        var result = await engine.RunAsync(this.Options(dryRun), deadline.Token);
        Assert.That(sourceStarted.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(attributesRead.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(result.Plan.Add, Has.Count.EqualTo(1));
        Assert.That(result.VerifiedBatches, Is.EqualTo(dryRun ? 0 : 2));
        Assert.That(files.Session.Disposed, Is.True);
        if (dryRun)
        {
            Assert.That(device.FileConnections.SelectMany(peer => peer.ReadPaths),
                Does.Not.Contain(SimulatedSyncCatalogue.ExistingTrackPath),
                "Early metadata reads must not trigger audio reads for unmatched sizes.");
        }
    }

    [Test]
    public async Task Failure_In_Either_Preparation_Cancels_And_Joins_The_Other([Values] bool sourceFails)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        var sourceStarted = NewSignal();
        var phoneStarted = NewSignal();
        var sourceStopped = NewSignal();
        var phoneStopped = NewSignal();
        using var device = new SimulatedSyncDevice();
        var files = new BudgetSourceFileSystem
        {
            BeforeRead = token =>
            {
                sourceStarted.TrySetResult();
                try
                {
                    phoneStarted.Task.WaitAsync(token).GetAwaiter().GetResult();
                    if (sourceFails) throw new IOException(FailureMessage);

                    Task.Delay(Timeout.InfiniteTimeSpan, token).GetAwaiter().GetResult();
                }
                finally
                {
                    sourceStopped.TrySetResult();
                }
            }
        };

        var engine = this.CreateEngine(device, files, async (_, _, _, token) =>
        {
            phoneStarted.TrySetResult();
            try
            {
                await sourceStarted.Task.WaitAsync(token);
                if (!sourceFails) throw new IOException(FailureMessage);

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The cancelled wait returned.");
            }
            finally
            {
                phoneStopped.TrySetResult();
            }
        });

        await Assert.ThatAsync(() => engine.RunAsync(this.Options(), deadline.Token),
            Throws.TypeOf<IOException>().With.Message.EqualTo(FailureMessage));

        Assert.That(deadline.IsCancellationRequested, Is.False);
        Assert.That(sourceStopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(phoneStopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(files.Session.Disposed, Is.True);
        Assert.That(files.Session.ActiveEnumerations, Is.Zero);
        Assert.That(device.StartedSessions, Is.Zero);
        Assert.That(device.Batches, Is.Empty);
    }

    [Test]
    public async Task A_Failed_Source_Scan_Does_Not_Start_Recovery_Of_A_Pending_Write()
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var device = new SimulatedSyncDevice { Interruption = SyncInterruptionPoint.AfterMetadata };
        await Assert.ThatAsync(() => device.RunAsync(
            this.source.Root, this.state.Root, cancellationToken: deadline.Token), Throws.TypeOf<IOException>());

        var connections = device.Connections.Count;
        var batches = device.Batches.Count;
        var sourceStarted = NewSignal();
        var releaseSource = NewSignal();
        var files = new BudgetSourceFileSystem
        {
            BeforeRead = token =>
            {
                sourceStarted.TrySetResult();
                releaseSource.Task.WaitAsync(token).GetAwaiter().GetResult();
                throw new IOException(FailureMessage);
            }
        };

        device.Interruption = SyncInterruptionPoint.None;
        var engine = this.CreateEngine(device, files, device.OpenAsync);
        var run = engine.RunAsync(this.Options(), deadline.Token);
        await sourceStarted.Task.WaitAsync(deadline.Token);
        try
        {
            Assert.That(device.Connections, Has.Count.EqualTo(connections));
        }
        finally
        {
            releaseSource.TrySetResult();
        }

        await Assert.ThatAsync(() => run, Throws.TypeOf<IOException>().With.Message.EqualTo(FailureMessage));
        Assert.That(device.Connections, Has.Count.EqualTo(connections));
        Assert.That(device.Batches, Has.Count.EqualTo(batches));
        Assert.That(files.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Caller_Cancellation_Stops_Both_Preparations_Before_Returning()
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var sourceStarted = NewSignal();
        var phoneStarted = NewSignal();
        var phoneStopped = NewSignal();
        using var device = new SimulatedSyncDevice();
        var files = new BudgetSourceFileSystem
        {
            BeforeRead = token =>
            {
                sourceStarted.TrySetResult();
                Task.Delay(Timeout.InfiniteTimeSpan, token).GetAwaiter().GetResult();
            }
        };

        var engine = this.CreateEngine(device, files, async (_, _, _, token) =>
        {
            phoneStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The cancelled wait returned.");
            }
            finally
            {
                phoneStopped.TrySetResult();
            }
        });

        var run = engine.RunAsync(this.Options(), cancellation.Token);
        await Task.WhenAll(sourceStarted.Task, phoneStarted.Task).WaitAsync(deadline.Token);
        await cancellation.CancelAsync();
        await Assert.ThatAsync(() => run.WaitAsync(deadline.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(phoneStopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(files.Session.Disposed, Is.True);
        Assert.That(device.Batches, Is.Empty);
    }

    private SyncEngine CreateEngine(
        SimulatedSyncDevice device,
        BudgetSourceFileSystem files,
        SyncConnectionFactory openConnection)
    {
        return new SyncEngine(
            new SourceScanner(files),
            files,
            device.CreateTransport(),
            TextWriter.Null)
        {
            OpenConnection = openConnection,
            DeviceHashCacheDirectory = this.cache.Root
        };
    }

    private SyncOptions Options(bool dryRun = false)
    {
        return new SyncOptions
        {
            Folder = this.source.Root,
            StateFolder = this.state.Root,
            Timeout = SimulatedSyncDevice.RequestTimeoutSeconds,
            DryRun = dryRun
        };
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
