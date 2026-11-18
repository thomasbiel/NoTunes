namespace NoTunes.Apple;

internal static class ArtworkDatabase
{
    internal const string Path = "iTunes_Control/Artwork/ArtworkDB";
    internal const int MaximumBytes = 32 * 1024 * 1024;

    public static async Task<uint> HighestAsync(AfcClient afc, uint floor, CancellationToken cancellationToken)
    {
        if (await afc.GetFileInfoAsync(Path, cancellationToken) is null) return floor;

        return Math.Max(floor, Highest(await afc.ReadFileAsync(Path, MaximumBytes, cancellationToken)));
    }

    internal static uint Highest(byte[] bytes)
    {
        return new ArtworkDatabaseReader(bytes).ReadHighest();
    }
}