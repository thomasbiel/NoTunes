namespace NoTunes.Music;

internal sealed record GroupSortNames(
    IReadOnlyDictionary<string, string?> Albums,
    IReadOnlyDictionary<string, string?> AlbumArtists,
    IReadOnlyDictionary<string, string?> Artists,
    IReadOnlyDictionary<string, string?> Composers)
{
    public static GroupSortNames Empty { get; } = new(
        new Dictionary<string, string?>(),
        new Dictionary<string, string?>(),
        new Dictionary<string, string?>(),
        new Dictionary<string, string?>());

    public bool Preserves(GroupSortNames expected)
    {
        return Preserves(this.Albums, expected.Albums) && Preserves(this.AlbumArtists, expected.AlbumArtists)
            && Preserves(this.Artists, expected.Artists) && Preserves(this.Composers, expected.Composers);
    }

    private static bool Preserves(
        IReadOnlyDictionary<string, string?> actual,
        IReadOnlyDictionary<string, string?> expected)
    {
        return expected.All(pair => actual.TryGetValue(pair.Key, out var value)
            && StringComparer.Ordinal.Equals(value, pair.Value));
    }
}
