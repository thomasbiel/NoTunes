namespace NoTunes.Apple;

public sealed record CatalogueSnapshot(byte[] Database, byte[]? Wal)
{
    internal const string DatabasePath = "iTunes_Control/iTunes/MediaLibrary.sqlitedb";
    private const string WalPath = DatabasePath + "-wal";
    private const string SharedMemoryPath = DatabasePath + "-shm";
    private const int MinimumSqliteHeaderBytes = 100;
    private const int MaxFileBytes = 64 * 1024 * 1024;
    private static readonly string[] Paths = [DatabasePath, WalPath, SharedMemoryPath];

    private static ReadOnlySpan<byte> SqliteSignature => "SQLite format 3\0"u8;

    public static async Task<CatalogueSnapshot> CaptureAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        var first = await CaptureOnce(afc, cancellationToken);
        var second = await CaptureOnce(afc, cancellationToken);
        if (!first.Snapshot.Database.AsSpan().SequenceEqual(second.Snapshot.Database) || !SameBytes(first.Snapshot.Wal, second.Snapshot.Wal) || !SameFileAttributes(first.Attributes, second.Attributes))
            throw new IOException("The iPhone music catalogue changed between verification reads. " + "Stop playback or other sync software and try again.");

        return second.Snapshot;
    }

    private static async Task<CatalogueCapture> CaptureOnce(AfcClient afc, CancellationToken cancellationToken)
    {
        var before = await StatAll(afc, cancellationToken);
        if (before[DatabasePath] is null) throw new IOException("The iPhone music catalogue is not initialized.");

        var database = await afc.ReadFileAsync(DatabasePath, MaxFileBytes, cancellationToken);
        var wal = before[WalPath] is null ? null : await afc.ReadFileAsync(WalPath, MaxFileBytes, cancellationToken);
        var after = await StatAll(afc, cancellationToken);
        if (!SameFileAttributes(before, after))
            throw new IOException("The iPhone music catalogue changed during capture. Try again after it settles.");
        if (database.Length < MinimumSqliteHeaderBytes || !database.AsSpan().StartsWith(SqliteSignature))
            throw new InvalidDataException("The iPhone music catalogue is not a valid SQLite database.");

        return new CatalogueCapture(new CatalogueSnapshot(database, wal), after);
    }

    private static bool SameBytes(byte[]? left, byte[]? right)
    {
        return left is null || right is null ? left is null && right is null : left.AsSpan().SequenceEqual(right);
    }

    private static bool SameFileAttributes(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>?> left, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>?> right)
    {
        return Paths.All(path => AfcClient.SameAttributes(left[path], right[path]));
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, string>?>> StatAll(AfcClient afc, CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.Ordinal);
        foreach (var path in Paths)
            attributes[path] = await afc.GetFileInfoAsync(path, cancellationToken);
        return attributes;
    }
}
