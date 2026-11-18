using System.Security.Cryptography;
using NoTunes.Platform;

namespace NoTunes.Apple;

internal static class DeviceStaging
{
    public static async Task WriteMetadataAsync(
        AfcClient afc,
        byte[] operation,
        byte[] signature,
        CancellationToken cancellationToken)
    {
        await WriteVerifiedAsync(
            afc,
            SyncPaths.Metadata,
            operation,
            cancellationToken);

        await WriteVerifiedAsync(
            afc,
            SyncPaths.Metadata + SyncPaths.SignatureSuffix,
            signature,
            cancellationToken);
    }

    public static async Task RemoveMetadataAsync(
        AfcClient afc,
        byte[] operation,
        byte[] signature,
        CancellationToken cancellationToken)
    {
        await RemoveMatchingAsync(
            afc,
            SyncPaths.Metadata,
            operation,
            cancellationToken);

        await RemoveMatchingAsync(
            afc,
            SyncPaths.Metadata + SyncPaths.SignatureSuffix,
            signature,
            cancellationToken);
    }

    public static async Task WriteVerifiedAsync(
        AfcClient afc,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await afc.WriteNewFileAsync(
            path,
            stream,
            bytes.Length,
            cancellationToken);

        if (!(await afc.ReadFileAsync(path, bytes.Length, cancellationToken)).AsSpan().SequenceEqual(bytes))
        {
            throw new IOException("Device staging failed read-back verification.");
        }
    }

    public static async Task RemoveMatchingAsync(
        AfcClient afc,
        string path,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(expected, writable: false);
        await RemoveMatchingAsync(
            afc,
            path,
            stream,
            cancellationToken);
    }

    public static async Task RemoveMatchingAsync(
        AfcClient afc,
        string path,
        string expectedFile,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(expectedFile);
        await RemoveMatchingAsync(
            afc,
            path,
            stream,
            cancellationToken);
    }

    private static async Task RemoveMatchingAsync(
        AfcClient afc,
        string path,
        Stream expected,
        CancellationToken cancellationToken)
    {
        var info = await afc.GetFileInfoAsync(path, cancellationToken);
        if (info is null) return;

        if (!ProtocolNumbers.TryParseNonNegativeInt64(info.GetValueOrDefault(AfcKeys.FileSize), out var length)
            || length > expected.Length)
        {
            throw new IOException("A recovery staging file is not owned by this operation: " + path);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SourceFileDefaults.ReadBufferBytes];
        var remaining = length;
        while (remaining > 0)
        {
            var count = await expected.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) throw new IOException("The durable recovery source ended unexpectedly.");

            hash.AppendData(buffer.AsSpan(0, count));
            remaining -= count;
        }

        var actual = await afc.HashFileAsync(path, length, cancellationToken);
        if (!StringComparer.Ordinal.Equals(actual, Convert.ToHexString(hash.GetHashAndReset()))
            || !AfcClient.SameAttributes(info, await afc.GetFileInfoAsync(path, cancellationToken)))
        {
            throw new IOException("Refusing to remove foreign or changed staging data: " + path);
        }

        await afc.RemoveStagingFileAsync(path, cancellationToken);
    }
}
