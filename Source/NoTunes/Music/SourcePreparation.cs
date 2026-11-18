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
        using var paths = fileSystem.OpenSource(new DirectoryInfo(library.Root), cancellationToken);
        var preparer = new SourceTrackPreparer(
            library,
            catalogue,
            paths,
            workspace,
            options,
            catalogue.OccupiedIds.ToHashSet());

        var progress = new OperationProgress(
            options.Error,
            options.ArtworkOnly ? "Checking and preparing covers" : "Preparing source tracks",
            plan.Add.Count);

        var result = new List<PreparedTrack>();
        var completed = 0;
        foreach (var track in plan.Add)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(completed++);
            var prepared = await preparer.PrepareAsync(track, cancellationToken);
            if (prepared is not null) result.Add(prepared);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(completed);
        return result;
    }
}
