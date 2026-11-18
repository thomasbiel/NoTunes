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
        var (allAdditions, allDeletions) = Changes(high);
        var document = SyncOperations.CreateDocument(catalogue, allAdditions, allDeletions);
        var operations = (object[])document[MediaKeys.Operations];
        var size = new BinaryPlistSize();
        if (Fits(high)) return CreateBatch(high);

        high--;
        var best = 0;
        while (low <= high)
        {
            var count = low + (high - low) / 2;
            if (!Fits(count))
            {
                high = count - 1;
            }
            else
            {
                best = count;
                low = count + 1;
            }
        }

        return best == 0
            ? throw new InvalidDataException("No track change fits within the sync operation size limit.")
            : CreateBatch(best);

        bool Fits(int count)
        {
            SelectOperations(count);
            return size.GetSize(document) <= SyncProtocol.MaxOperationBytes;
        }

        SyncBatch CreateBatch(int count)
        {
            var (add, delete) = Changes(count);
            SelectOperations(count);
            var operation = BinaryPlist.WriteWithinLimit(document, SyncProtocol.MaxOperationBytes)
                ?? throw new InvalidDataException("Sync operation size changed during serialization.");

            return new SyncBatch(add, delete, operation);
        }

        (PreparedTrack[] Add, DeviceTrack[] Delete) Changes(int count)
        {
            var addCount = Math.Min(count, additions.Count);
            var add = additions.Take(addCount).ToArray();
            var delete = deletions.Take(count - addCount).ToArray();
            return (add, delete);
        }

        void SelectOperations(int count)
        {
            var operationCount = count + 1;
            document[MediaKeys.Operations] = operationCount == operations.Length ? operations : operations[..operationCount];
        }
    }
}
