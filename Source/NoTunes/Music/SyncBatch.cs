using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record SyncBatch(
    IReadOnlyList<PreparedTrack> Additions,
    IReadOnlyList<DeviceTrack> Deletions,
    byte[] Operation)
{
    public static SyncBatch Create(
        DeviceCatalogue catalogue,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlyList<DeviceTrack> deletions)
    {
        var low = 1;
        var high = Math.Min(SyncProtocol.MaxTrackChanges, additions.Count + deletions.Count);
        var full = CreateCandidate(high);
        if (full is not null) return full;

        high--;
        SyncBatch? best = null;
        while (low <= high)
        {
            var count = low + (high - low) / 2;
            var candidate = CreateCandidate(count);
            if (candidate is null)
            {
                high = count - 1;
            }

            else
            {
                best = candidate;
                low = count + 1;
            }
        }

        return best ?? throw new InvalidDataException("No track change fits within the sync operation size limit.");

        SyncBatch? CreateCandidate(int count)
        {
            var addCount = Math.Min(count, additions.Count);
            var add = additions.Take(addCount).ToArray();
            var delete = deletions.Take(count - addCount).ToArray();
            var operation = SyncOperations.SerializeWithinLimit(catalogue, add, delete);
            return operation is null ? null : new SyncBatch(add, delete, operation);
        }
    }
}
