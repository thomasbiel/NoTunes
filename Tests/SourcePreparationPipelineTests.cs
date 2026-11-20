using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SourcePreparationPipelineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const string FailureMessage = "Injected pipeline failure.";
    private const int TrackCount = 4;
    private const int NextCopyRead = 2;
    private SyncWorkspace source = null!;
    private SyncWorkspace output = null!;
    private SourceLibrary library = null!;
    private CancellationTokenSource deadline = null!;
    private int reads;

    [SetUp]
    public async Task SetUp()
    {
        this.source = new SyncWorkspace();
        this.output = new SyncWorkspace();
        this.deadline = new CancellationTokenSource(TestTimeout);
        this.reads = 0;
        SourceTestData.WriteSyncTracks(this.source.Root, TrackCount);
        this.library = await SourceTestData.ScanAsync(this.source.Root);
    }

    [TearDown]
    public void TearDown()
    {
        this.deadline.Dispose();
        this.output.Dispose();
        this.source.Dispose();
    }

    [Test]
    public async Task Copies_Only_The_Next_Track_While_Reading_Metadata_And_Preserves_Order()
    {
        var metadataStarted = NewSignal();
        var nextCopyStarted = NewSignal();
        var files = this.ObserveNextCopy(token =>
        {
            metadataStarted.Task.WaitAsync(token).GetAwaiter().GetResult();
            nextCopyStarted.TrySetResult();
        });

        var metadataOrder = new List<string>();
        var result = await this.PrepareAsync(
            files,
            new SourcePreparationOptions
            {
                ReadMetadata = (path, relativePath, cache) =>
                {
                    metadataOrder.Add(relativePath);
                    if (metadataOrder.Count == 1)
                    {
                        metadataStarted.TrySetResult();
                        nextCopyStarted.Task.WaitAsync(this.deadline.Token).GetAwaiter().GetResult();
                        Assert.That(Volatile.Read(ref this.reads), Is.EqualTo(NextCopyRead),
                            "Copying must stay one track ahead.");
                    }

                    return Mp3Metadata.Read(path, relativePath, cache);
                }
            });

        Assert.That(metadataOrder, Is.EqualTo(this.library.Tracks.Select(track => track.RelativePath)));
        Assert.That(result.Select(track => track.Source), Is.EqualTo(this.library.Tracks));
        Assert.That(result.Select(track => track.Id).Distinct().Count(), Is.EqualTo(TrackCount));
        Assert.That(result.All(track => track.Workspace == this.output), Is.True);
        this.AssertPreparationStopped(files);
    }

    [Test]
    public async Task Metadata_Failure_Or_Cancellation_Joins_The_Next_Copy_Before_Disposing_The_Source(
        [Values] bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.deadline.Token);
        var nextCopyStarted = NewSignal();
        var nextCopyStopped = NewSignal();
        var files = this.ObserveNextCopy(token =>
        {
            nextCopyStarted.TrySetResult();
            try
            {
                Task.Delay(Timeout.InfiniteTimeSpan, token).GetAwaiter().GetResult();
            }
            finally
            {
                nextCopyStopped.TrySetResult();
            }
        });

        var failure = await Assert.CatchAsync<Exception>(() => this.PrepareAsync(
            files,
            new SourcePreparationOptions
            {
                ReadMetadata = (path, relativePath, cache) =>
                {
                    nextCopyStarted.Task.WaitAsync(this.deadline.Token).GetAwaiter().GetResult();
                    if (!cancel) throw new IOException(FailureMessage);

                    cancellation.Cancel();
                    return Mp3Metadata.Read(path, relativePath, cache);
                }
            },
            cancellationToken: cancellation.Token));

        Assert.That(failure, cancel ? Is.InstanceOf<OperationCanceledException>() : Is.TypeOf<IOException>());
        if (!cancel) Assert.That(failure!.Message, Is.EqualTo(FailureMessage));

        Assert.That(nextCopyStopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(this.reads, Is.EqualTo(NextCopyRead));
        this.AssertPreparationStopped(files);
    }

    [Test]
    public async Task Copy_Failure_Cancels_An_Active_Group_Prompt_And_Preserves_The_Source_Error()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("INSERT INTO album VALUES(13,1013,'Album',11,'Other Sort');");
        var catalogue = await fixture.ReadAsync();
        var promptStarted = NewSignal();
        var promptStopped = NewSignal();
        var files = this.ObserveNextCopy(token =>
        {
            promptStarted.Task.WaitAsync(token).GetAwaiter().GetResult();
            throw new IOException(FailureMessage);
        });

        await Assert.ThatAsync(() => this.PrepareAsync(
            files,
            new SourcePreparationOptions
            {
                Prompt = async (_, token) =>
                {
                    promptStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return null;
                    }
                    finally
                    {
                        promptStopped.TrySetResult();
                    }
                }
            },
            catalogue), Throws.TypeOf<IOException>().With.Message.EqualTo(FailureMessage));

        Assert.That(promptStopped.Task.IsCompletedSuccessfully, Is.True);
        this.AssertPreparationStopped(files);
    }

    private Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        BudgetSourceFileSystem files,
        SourcePreparationOptions options,
        DeviceCatalogue? catalogue = null,
        CancellationToken? cancellationToken = null)
    {
        catalogue ??= CatalogueTestData.Create([]);
        var plan = new SyncPlan(
            "phone",
            catalogue.Anchor,
            this.library.Tracks,
            [],
            0);

        return SourcePreparation.PrepareAsync(
            this.library,
            plan,
            catalogue,
            files,
            this.output,
            cancellationToken ?? this.deadline.Token,
            options);
    }

    private BudgetSourceFileSystem ObserveNextCopy(Action<CancellationToken> beforeRead)
    {
        return new BudgetSourceFileSystem
        {
            BeforeRead = token =>
            {
                if (Interlocked.Increment(ref this.reads) == NextCopyRead) beforeRead(token);
            }
        };
    }

    private void AssertPreparationStopped(BudgetSourceFileSystem files)
    {
        Assert.That(this.deadline.IsCancellationRequested, Is.False,
            "The pipeline must stop without waiting for the timeout.");

        Assert.That(files.Session.Disposed, Is.True);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
