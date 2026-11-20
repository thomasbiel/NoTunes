using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class SourceTrackPreparer(
    DeviceCatalogue catalogue,
    SyncWorkspace workspace,
    SourcePreparationOptions options,
    ISet<long> occupied)
{
    private const long UnusedGroupId = 0;
    private readonly ArtworkCache artworkCache = new(workspace.Root);
    private readonly SourceGroupResolver groups = new(catalogue, occupied, options.Prompt);
    private uint artworkId = catalogue.ArtworkHighWaterMark;

    public async Task<PreparedTrack?> PrepareAsync(CopiedSourceTrack copy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (copy.Skipped is { } skipped) options.OnSkipped!(skipped);
        if (copy.LocalPath is not { } localPath) return null;

        var track = copy.Source;
        var metadata = this.ReadMetadata(track, localPath);
        if (metadata is null || options.ArtworkOnly && metadata.Artwork is null)
        {
            File.Delete(localPath);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
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
            resolved.ArtistId)
        {
            Workspace = workspace
        };
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

    private Mp3Metadata? ReadMetadata(SourceTrack track, string localPath)
    {
        try
        {
            return options.ReadMetadata(localPath, track.RelativePath, this.artworkCache);
        }
        catch (Exception exception) when (options.TryDescribeSkippedFile(exception, track, out var skipped))
        {
            options.OnSkipped!(skipped);
            return null;
        }
    }
}
