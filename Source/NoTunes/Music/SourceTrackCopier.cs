using NoTunes.Apple;
using NoTunes.Platform;

namespace NoTunes.Music;

internal sealed class SourceTrackCopier(
    SourceLibrary library,
    ISourceSession paths,
    SyncWorkspace workspace,
    SourcePreparationOptions options)
{
    public async Task<CopiedSourceTrack> CopyAsync(SourceTrack track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = SourceDirectoryLease.Acquire(paths, library.Root, track.RelativePath);
        if (options.ArtworkOnly)
        {
            using var guarded = paths.OpenRead(new FileInfo(lease.FilePath));
            var hasArtwork = false;
            SkippedSourceFile? skipped = null;
            try
            {
                hasArtwork = Mp3Metadata.HasArtwork(guarded);
            }
            catch (Exception exception) when (options.TryDescribeSkippedFile(exception, track, out var failure))
            {
                skipped = failure;
            }

            RequireUnchanged(track, guarded, "while checking artwork");
            if (!hasArtwork) return new CopiedSourceTrack(track, null, skipped);
        }

        var localPath = Path.Combine(workspace.Root, Guid.NewGuid().ToString("N") + SyncPaths.Mp3Extension);
        await using (var input = paths.OpenRead(new FileInfo(lease.FilePath)))
        await using (var output = new FileStream(
            localPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            RequireUnchanged(track, input, "after scanning");
            await input.CopyToAsync(output, cancellationToken);
            RequireUnchanged(track, input, "during preparation");
        }

        await using var copy = File.OpenRead(localPath);
        var actual = await ContentHash.ComputeAsync(copy, cancellationToken);
        if (copy.Length != track.Size || !StringComparer.Ordinal.Equals(actual, track.Hash))
        {
            throw new IOException($"Source bytes changed after scanning: {track.RelativePath}");
        }

        return new CopiedSourceTrack(track, localPath);
    }

    private static void RequireUnchanged(SourceTrack track, FileStream stream, string phase)
    {
        if (stream.Length != track.Size || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != track.LastWriteTimeUtc)
        {
            throw new IOException($"Source changed {phase}: {track.RelativePath}");
        }
    }
}
