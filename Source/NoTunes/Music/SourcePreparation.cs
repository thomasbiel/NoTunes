using System.Runtime.ExceptionServices;
using NoTunes.Platform;

namespace NoTunes.Music;

internal static class SourcePreparation
{
    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        SourceLibrary library,
        SyncPlan plan,
        DeviceCatalogue catalogue,
        ISourceFileSystem fileSystem,
        SyncWorkspace workspace,
        CancellationToken cancellationToken,
        SourcePreparationOptions? options = null)
    {
        options ??= new SourcePreparationOptions();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var paths = fileSystem.OpenSource(new DirectoryInfo(library.Root), stopping.Token);
        var copier = new SourceTrackCopier(
            library,
            paths,
            workspace,
            options);

        var preparer = new SourceTrackPreparer(
            catalogue,
            workspace,
            options,
            catalogue.OccupiedIds.ToHashSet());

        var progress = new OperationProgress(
            options.Error,
            options.ArtworkOnly ? "Checking and preparing covers" : "Preparing source tracks",
            plan.Add.Count);

        var result = new List<PreparedTrack>();
        ExceptionDispatchInfo? copyFailure = null;
        Task<CopiedSourceTrack>? pending = null;
        try
        {
            if (plan.Add.Count > 0) pending = StartCopy(plan.Add[0]);
            for (var index = 0; index < plan.Add.Count; index++)
            {
                stopping.Token.ThrowIfCancellationRequested();
                progress.Report(index);
                var copy = await pending!;

                // Keep only one copy ahead; metadata, prompts and ID allocation remain ordered.
                pending = index + 1 < plan.Add.Count ? StartCopy(plan.Add[index + 1]) : null;
                var prepared = await preparer.PrepareAsync(copy, stopping.Token);
                if (prepared is not null) result.Add(prepared);
            }

            stopping.Token.ThrowIfCancellationRequested();
            progress.Report(plan.Add.Count);
            return result;
        }
        catch (Exception exception)
        {
            await stopping.CancelAsync();
            if (pending is not null)
            {
                try
                {
                    await pending;
                }
                catch
                {
                    // Observe and finish the copy before disposing its source session or workspace.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (exception is OperationCanceledException) copyFailure?.Throw();

            throw;
        }

        Task<CopiedSourceTrack> StartCopy(SourceTrack track)
        {
            return Task.Run(async () =>
            {
                try
                {
                    return await copier.CopyAsync(track, stopping.Token);
                }
                catch (Exception exception)
                {
                    copyFailure = ExceptionDispatchInfo.Capture(exception);
                    await stopping.CancelAsync();
                    throw;
                }
            }, stopping.Token);
        }
    }
}
