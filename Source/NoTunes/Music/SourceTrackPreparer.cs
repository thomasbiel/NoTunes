using System.Security.Cryptography;
using NoTunes.Apple;
using NoTunes.Platform;

namespace NoTunes.Music;

internal sealed class SourceTrackPreparer(
    SourceLibrary library,
    DeviceCatalogue catalogue,
    ISourceSession paths,
    SyncWorkspace workspace,
    SourcePreparationOptions options,
    ISet<long> occupied)
{
    private const long UnusedGroupId = 0;
    private readonly ArtworkCache artworkCache = new(workspace.Root);
    private readonly SourceGroupResolver groups = new(catalogue, occupied, options.Prompt);
    private uint artworkId = catalogue.ArtworkHighWaterMark;

    public async Task<PreparedTrack?> PrepareAsync(SourceTrack track, CancellationToken cancellationToken)
    {
        using var lease = SourceDirectoryLease.Acquire(paths, library.Root, track.RelativePath);
        if (options.ArtworkOnly && !this.HasArtwork(track, lease.FilePath)) return null;

        var localPath = await this.CopyVerifiedAsync(track, lease.FilePath, cancellationToken);
        if (!this.TryReadMetadata(
            track,
            () => Mp3Metadata.Read(localPath, track.RelativePath, this.artworkCache),
            out var metadata)
            || options.ArtworkOnly && metadata.Artwork is null)
        {
            File.Delete(localPath);
            return null;
        }

        metadata = this.AllocateArtwork(metadata);
        var resolved = options.ArtworkOnly
            ? new ResolvedTrackGroups(metadata, UnusedGroupId, UnusedGroupId)
            : await this.groups.ResolveAsync(metadata, track.RelativePath, cancellationToken);

        return new PreparedTrack(
            track,
            localPath,
            resolved.Metadata,
            LibraryPreferences.NewId(occupied),
            resolved.AlbumId,
            resolved.ArtistId);
    }

    private bool HasArtwork(SourceTrack track, string fullPath)
    {
        using var guarded = paths.OpenRead(new FileInfo(fullPath));
        var readable = this.TryReadMetadata(track, () => Mp3Metadata.HasArtwork(guarded), out var hasArtwork);
        RequireUnchanged(track, guarded, "while checking artwork");
        return readable && hasArtwork;
    }

    private async Task<string> CopyVerifiedAsync(
        SourceTrack track,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(workspace.Root, Guid.NewGuid().ToString("N") + SyncPaths.Mp3Extension);
        await using (var input = paths.OpenRead(new FileInfo(fullPath)))
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
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken));
        if (copy.Length != track.Size || !StringComparer.Ordinal.Equals(actual, track.Sha256))
        {
            throw new IOException($"Source bytes changed after scanning: {track.RelativePath}");
        }

        return localPath;
    }

    private static void RequireUnchanged(SourceTrack track, FileStream stream, string phase)
    {
        if (stream.Length != track.Size || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != track.LastWriteTimeUtc)
        {
            throw new IOException($"Source changed {phase}: {track.RelativePath}");
        }
    }

    private Mp3Metadata AllocateArtwork(Mp3Metadata metadata)
    {
        if (metadata.Artwork is not { } artwork) return metadata;
        if (!catalogue.ArtworkAvailable) throw new IOException("This catalogue cannot register artwork.");

        metadata = metadata with { Artwork = artwork with { CacheId = ArtworkImage.Allocate(ref this.artworkId) } };
        metadata.Item[MediaKeys.ArtworkChecksum] = (long)artwork.Checksum;
        metadata.Item[MediaKeys.ArtworkCacheId] = (long)this.artworkId;
        return metadata;
    }

    private bool TryReadMetadata<T>(SourceTrack track, Func<T> read, out T metadata)
    {
        try
        {
            metadata = read();
            return true;
        }
        catch (Exception exception) when (options.OnSkipped is not null
            && exception is InvalidDataException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            options.OnSkipped(new SkippedSourceFile(
                track.RelativePath,
                exception.GetBaseException().Message,
                options.ArtworkOnly));

            metadata = default!;
            return false;
        }
    }
}
