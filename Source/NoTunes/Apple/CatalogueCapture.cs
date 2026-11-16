namespace NoTunes.Apple;

internal sealed record CatalogueCapture(CatalogueSnapshot Snapshot, Dictionary<string, IReadOnlyDictionary<string, string>?> Attributes);
