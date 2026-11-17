using NoTunes.Apple;

namespace NoTunes.Music;

internal static class SyncRecovery
{
    public static IReadOnlyList<DeviceTrack> Assess(
        PendingSync pending,
        DeviceCatalogue actual,
        IReadOnlyList<PreparedTrack> additions)
    {
        var before = pending.Before.Restore();
        var beforeIds = before.Tracks.Select(track => track.Id).ToHashSet();
        if (pending.Deletions.Any(track => !beforeIds.Contains(track.Id))
            || additions.Any(track => track.IsArtworkOnly ? !beforeIds.Contains(track.Id)
                : before.OccupiedIds.Contains(track.Id)))
            throw new InvalidDataException("The recovery operation contains an unowned or colliding identity.");

        var operation = Plist.Parse(pending.Operation).AsDict();
        var rebuilt = Plist.Parse(SyncOperations.Serialize(before, additions, pending.Deletions)).AsDict();
        if (operation.Number(MediaKeys.Revision) != rebuilt.Number(MediaKeys.Revision)
            || !BinaryPlist.Write(operation[MediaKeys.Operations]).AsSpan()
                .SequenceEqual(BinaryPlist.Write(rebuilt[MediaKeys.Operations])))
            throw new InvalidDataException("The recovery journal does not match its signed operation.");

        var pendingDeletionIds = pending.Deletions.Select(track => track.Id).ToHashSet();
        var remainingDeletions = actual.Tracks
            .Where(track => track.IsIncomplete && pendingDeletionIds.Contains(track.Id)).ToArray();
        if (actual.Anchor == before.Anchor)
        {
            SyncEngine.RequireUnchanged(before, actual);
            return remainingDeletions;
        }

        var expected = additions.ToDictionary(track => track.Id);
        foreach (var track in actual.Tracks.Where(track => expected.ContainsKey(track.Id)))
        {
            var source = expected[track.Id];
            if (!source.IsArtworkOnly && track.Title != (string)source.Metadata.Item[MediaKeys.Title]
                || !track.IsIncomplete && (track.Path != source.DevicePath || track.Size != source.Source.Size))
                throw new IOException("An interrupted insertion no longer matches its recorded identity.");
        }

        var incomplete = actual.Tracks.Where(track => expected.ContainsKey(track.Id) && track.IsIncomplete).ToArray();
        var incompleteIds = incomplete.Select(track => track.Id).ToHashSet();
        var projected = actual with
        {
            Tracks = [.. actual.Tracks.Where(track => !incompleteIds.Contains(track.Id))]
        };
        var presentIds = actual.Tracks.Select(track => track.Id).ToHashSet();
        var appliedDeletions = pending.Deletions.Where(track => !presentIds.Contains(track.Id)).ToArray();
        SyncEngine.VerifyResult(before, projected,
            [.. additions.Where(track => presentIds.Contains(track.Id) && !incompleteIds.Contains(track.Id))],
            appliedDeletions);
        return [.. incomplete, .. remainingDeletions];
    }
}
