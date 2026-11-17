namespace NoTunes.Music;

internal static class SyncPlanner
{
    public static async Task<SyncPlan> CreateAsync(
        string device,
        SourceLibrary source,
        DeviceCatalogue catalogue,
        Func<DeviceTrack, CancellationToken, Task<string?>> readHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceSizes = source.Tracks.Select(track => track.Size).ToHashSet();
        var byHash = new Dictionary<string, Queue<DeviceTrack>>(StringComparer.Ordinal);
        foreach (var track in catalogue.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track.IsIncomplete || !sourceSizes.Contains(track.Size))
                continue;
            var hash = await readHash(track, cancellationToken);
            if (hash is null)
                continue;
            if (!byHash.TryGetValue(hash, out var matches))
                byHash[hash] = matches = new Queue<DeviceTrack>();
            matches.Enqueue(track);
        }

        var retained = new HashSet<long>();
        var sourceMatches = new List<SourceMatch>();
        var additions = new List<SourceTrack>();
        foreach (var track in source.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byHash.TryGetValue(track.Sha256, out var matches) && matches.TryDequeue(out var match))
            {
                retained.Add(match.Id);
                sourceMatches.Add(new SourceMatch(track, match));
            }
            else
                additions.Add(track);
        }

        return new SyncPlan(
            device,
            catalogue.Anchor,
            additions,
            [.. catalogue.Tracks.Where(track => !retained.Contains(track.Id))],
            retained.Count) { Matches = sourceMatches };
    }
}
