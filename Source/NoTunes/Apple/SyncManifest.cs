using System.Globalization;
using NoTunes.Music;

namespace NoTunes.Apple;

internal static class SyncManifest
{
    public static IReadOnlyList<SyncAsset> Validate(
        Dictionary<string, object> parameters,
        IReadOnlyList<PreparedTrack> additions,
        IReadOnlySet<long> retained)
    {
        var manifest = parameters.Dictionary(AtcKeys.AssetManifest);
        if (manifest.Keys.Any(key => key != AtcKeys.Media))
        {
            throw new InvalidDataException("Unsupported ATC asset manifest keys: "
                + string.Join(", ", manifest.Keys.Order(StringComparer.Ordinal)) + ".");
        }

        var media = manifest.GetValueOrDefault(AtcKeys.Media);
        if (media is not object[] entries)
        {
            throw new InvalidDataException($"ATC asset manifest {AtcKeys.Media} must be an array; "
                + $"received {media?.GetType().Name ?? "missing or null"}.");
        }

        // Each known track ID may occur once; artwork-only updates also belong to the retained set.
        var maximumEntries = (long)retained.Count + additions.Count(track => !retained.Contains(track.Id));
        if (entries.Length > maximumEntries)
        {
            throw new InvalidDataException($"ATC asset manifest contains {entries.Length} entries, "
                + $"but only {maximumEntries} distinct track IDs are expected "
                + $"({additions.Count} additions/updates, {retained.Count} retained).");
        }

        var own = additions.ToDictionary(track => track.AssetId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SyncAsset>();
        foreach (var value in entries)
        {
            var entry = value.AsDict();
            var id = entry.Text(AtcKeys.AssetId);
            var parts = entry.Dictionary(AtcKeys.Variant).Number(AtcKeys.AssetParts);
            if (entry.Text(AtcKeys.AssetType) != AtcKeys.Music || !entry.Flag(AtcKeys.IsDownload) || !seen.Add(id))
            {
                throw new InvalidDataException("Invalid or duplicate asset request.");
            }

            if (own.TryGetValue(id, out var track)
                && (track.IsArtworkOnly
                    ? parts is SyncProtocol.ArtworkPart or (SyncProtocol.ArtworkPart | SyncProtocol.GeniusPart)
                    : track.Metadata.Artwork is null
                    ? parts is SyncProtocol.AudioPart or SyncProtocol.AudioWithGenius
                    : parts is (SyncProtocol.AudioPart | SyncProtocol.ArtworkPart)
                        or (SyncProtocol.AudioWithGenius | SyncProtocol.ArtworkPart)))
                result.Add(new SyncAsset(id, track));
            else if (long.TryParse(
                id,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var pid)
                && StringComparer.Ordinal.Equals(id, pid.ToString(CultureInfo.InvariantCulture))
                && retained.Contains(pid) && parts is SyncProtocol.ArtworkPart or SyncProtocol.GeniusPart)
                result.Add(new SyncAsset(id, null));
            else
                throw new InvalidDataException($"Unexpected asset request: {id}, parts {parts}.");
        }

        if (own.Keys.Any(id => !seen.Contains(id)))
        {
            throw new InvalidDataException("The phone did not request every new audio asset.");
        }

        return result;
    }
}
