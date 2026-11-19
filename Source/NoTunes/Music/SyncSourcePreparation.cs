using NoTunes.Platform;

namespace NoTunes.Music;

internal sealed class SyncSourcePreparation(
    ISourceFileSystem fileSystem,
    GroupNamePrompt? prompt,
    TextWriter error)
{
    public async Task<PreparedSync> PrepareAsync(
        SourceLibrary source,
        SyncPlan plan,
        DeviceCatalogue catalogue,
        SyncWorkspace workspace,
        bool repairCovers,
        CancellationToken cancellationToken)
    {
        var skipped = new List<SkippedSourceFile>();
        await error.WriteLineAsync("Preparing verified local copies and MP3 metadata before changing the iPhone...");
        var prepared = await SourcePreparation.PrepareAsync(
            source,
            plan,
            catalogue,
            fileSystem,
            workspace,
            cancellationToken,
            new SourcePreparationOptions
            {
                Prompt = prompt,
                OnSkipped = ReportSkipped,
                Error = error
            });

        IReadOnlyList<PreparedTrack> artworkUpdates = [];
        if (repairCovers)
        {
            var missingArtwork = plan.Matches
                .Where(match => !catalogue.TrackArtwork.TryGetValue(match.Device.Id, out var artwork) || artwork.Path is null)
                .ToArray();

            var lastArtworkId = prepared
                .Where(track => track.Metadata.Artwork is not null)
                .Select(track => track.Metadata.Artwork!.CacheId)
                .DefaultIfEmpty(catalogue.ArtworkHighWaterMark)
                .Max();

            var repairs = await SourcePreparation.PrepareAsync(
                source,
                plan with { Add = [.. missingArtwork.Select(match => match.Source)] },
                catalogue with { ArtworkHighWaterMark = lastArtworkId },
                fileSystem,
                workspace,
                cancellationToken,
                new SourcePreparationOptions { ArtworkOnly = true, OnSkipped = ReportSkipped, Error = error });

            var matches = missingArtwork.ToDictionary(match => match.Source.RelativePath, StringComparer.Ordinal);
            artworkUpdates = repairs
                .Select(track => track with
                {
                    Id = matches[track.Source.RelativePath].Device.Id,
                    IsArtworkOnly = true,
                    ExistingDevicePath = matches[track.Source.RelativePath].Device.Path
                })
                .ToArray();
        }

        plan = plan with
        {
            Add = [.. prepared.Select(track => track.Source)],
            Artwork = [.. artworkUpdates.Select(track => track.Source)],
            Skipped = skipped,
            DeferredDeletions = skipped.Count == 0 ? [] : plan.Delete,
            Delete = skipped.Count == 0 ? plan.Delete : []
        };

        if (plan.DeferredDeletions.Count != 0)
        {
            await error.WriteLineAsync(
                $"Deferred {DisplayFormat.Number(plan.DeferredDeletions.Count)} deletion(s) "
                + "because source files were skipped.");
        }

        return new PreparedSync(plan, prepared, artworkUpdates);

        void ReportSkipped(SkippedSourceFile file)
        {
            skipped.Add(file);
            var operation = file.ArtworkOnly ? "cover repair" : "source file";
            error.WriteLine($"Skipped {operation}: {file.RelativePath}: {file.Reason}");
        }
    }
}
