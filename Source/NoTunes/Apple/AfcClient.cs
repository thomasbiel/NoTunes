using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace NoTunes.Apple;

public sealed class AfcClient(Stream stream, TimeSpan timeout) : IAsyncDisposable
{
    private const int MaximumPathLength = 1024;
    private const int HeaderBytes = 5 * sizeof(ulong);
    private const int TotalLengthOffset = sizeof(ulong);
    private const int ThisLengthOffset = 2 * sizeof(ulong);
    private const int SequenceOffset = 3 * sizeof(ulong);
    private const int OperationOffset = 4 * sizeof(ulong);
    private const int MaxReadChunkBytes = 1024 * 1024;
    private const string RegularFileType = "S_IFREG";

    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private static readonly IEqualityComparer<KeyValuePair<string, string>> AttributeComparer =
        EqualityComparer<KeyValuePair<string, string>>.Create(
            (left, right) =>
                StringComparer.Ordinal.Equals(left.Key, right.Key) &&
                StringComparer.Ordinal.Equals(left.Value, right.Value),
            pair => HashCode.Combine(pair.Key, pair.Value));

    private static ReadOnlySpan<byte> Magic => "CFA6LPAA"u8;

    private ulong sequence;

    internal bool IsBroken { get; private set; }

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        return ParseFields(await this.RequestAsync(AfcOperation.GetDeviceInfo, [], cancellationToken));
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(path + '\0');
            return ParseFields(await this.RequestAsync(AfcOperation.GetFileInfo, body, cancellationToken));
        }
        catch (AfcException e) when (e.Code == (ulong)AfcStatusCode.ObjectNotFound)
        {
            return null;
        }
    }

    public async Task<byte[]> ReadFileAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await this.CopyFileToAsync(
            path,
            output,
            maxBytes,
            cancellationToken);

        return output.ToArray();
    }

    public async Task CopyFileToAsync(string path, Stream output, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var before = await this.GetFileInfoAsync(path, cancellationToken)
            ?? throw new FileNotFoundException($"iPhone file not found: {path}");

        if (before.GetValueOrDefault(AfcKeys.FileType) != RegularFileType
            || !ProtocolNumbers.TryParseNonNegativeInt64(before.GetValueOrDefault(AfcKeys.FileSize), out var size)
            || size > maxBytes)
        {
            throw new IOException($"iPhone file is not a regular file within the {maxBytes}-byte read limit: {path}");
        }

        var handle = await this.OpenFileAsync(path, AfcOpenMode.ReadOnly, cancellationToken);
        var success = false;
        try
        {
            long offset = 0;
            while (offset < size)
            {
                var count = (int)Math.Min(MaxReadChunkBytes, size - offset);
                var read = new byte[2 * sizeof(ulong)];
                BinaryPrimitives.WriteUInt64LittleEndian(read, handle);
                BinaryPrimitives.WriteUInt64LittleEndian(read.AsSpan(sizeof(ulong)), (ulong)count);
                var chunk = await this.RequestAsync(AfcOperation.FileRead, read, cancellationToken);
                if (chunk.Length == 0 || chunk.Length > count)
                {
                    throw new InvalidDataException($"Unexpected AFC file read length: {path}");
                }

                await output.WriteAsync(chunk, cancellationToken);
                offset += chunk.Length;
            }

            success = true;
        }
        finally
        {
            await this.CloseAfterOperationAsync(handle, success, cancellationToken);
        }

        var after = await this.GetFileInfoAsync(path, cancellationToken);
        if (!SameAttributes(before, after)) throw new IOException($"iPhone file changed while being read: {path}");
    }

    public async Task<IReadOnlyList<string>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var body = Encoding.UTF8.GetBytes(path + '\0');
        var bytes = await this.RequestAsync(AfcOperation.ReadDirectory, body, cancellationToken);
        if (bytes.Length == 0 || bytes[^1] != 0) throw new InvalidDataException("Invalid AFC directory listing.");

        return
        [
            .. Encoding.UTF8.GetString(bytes.AsSpan(0, bytes.Length - 1)).Split('\0')
                .Where(name => name.Length != 0 && name is not AfcKeys.CurrentDirectory and not AfcKeys.ParentDirectory)
        ];
    }

    internal async Task RemoveStagingFileAsync(string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        if (path != SyncPaths.Metadata &&
            path != SyncPaths.Metadata + SyncPaths.SignatureSuffix &&
            path != SyncPaths.Preferences &&
            path != SyncPaths.PreferencesPlist &&
            !path.StartsWith(SyncPaths.Airlock, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only verified staging or initialization files may be removed.");
        }

        await this.RequestAsync(
            AfcOperation.RemovePath,
            Encoding.UTF8.GetBytes(path + '\0'),
            cancellationToken,
            AfcOperation.Status);
    }

    public async Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var info = await this.GetFileInfoAsync(path, cancellationToken);
        if (info is not null)
        {
            if (info.GetValueOrDefault(AfcKeys.FileType) != AfcKeys.DirectoryType)
            {
                throw new IOException($"Expected an iPhone directory: {path}");
            }

            return;
        }

        var slash = path.LastIndexOf('/');
        if (slash > 0) await this.EnsureDirectoryAsync(path[..slash], cancellationToken);
        await this.RequestAsync(
            AfcOperation.MakeDirectory,
            Encoding.UTF8.GetBytes(path + '\0'),
            cancellationToken,
            AfcOperation.Status);
    }

    public async Task WriteNewFileAsync(
        string path,
        Stream input,
        long size,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task>? progress = null)
    {
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (await this.GetFileInfoAsync(path, cancellationToken) is not null)
        {
            throw new IOException($"Refusing to overwrite an existing iPhone file: {path}");
        }

        var handle = await this.OpenFileAsync(path, AfcOpenMode.WriteTruncate, cancellationToken);
        var success = false;
        try
        {
            var buffer = new byte[sizeof(ulong) + MaxReadChunkBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, handle);
            var remaining = size;
            while (remaining > 0)
            {
                var chunk = buffer.AsMemory(sizeof(ulong), (int)Math.Min(remaining, MaxReadChunkBytes));
                var count = await input.ReadAsync(chunk, cancellationToken);
                if (count == 0) throw new IOException("Source ended before the declared transfer size.");

                await this.RequestAsync(
                    AfcOperation.FileWrite,
                    buffer[..(sizeof(ulong) + count)],
                    cancellationToken,
                    AfcOperation.Status,
                    sizeof(ulong));

                remaining -= count;
                if (progress is not null)
                {
                    await progress(size - remaining, cancellationToken);
                }
            }

            if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
            {
                throw new IOException("Source exceeds the declared transfer size.");
            }

            success = true;
        }
        finally
        {
            await this.CloseAfterOperationAsync(handle, success, cancellationToken);
        }
    }

    private async ValueTask CloseAfterOperationAsync(ulong handle, bool success, CancellationToken cancellationToken)
    {
        if (this.IsBroken) return;

        using var deadline = new CancellationTokenSource(CloseTimeout);
        try
        {
            await this.CloseFileAsync(handle, deadline.Token);
        }
        catch (OperationCanceledException exception) when (
            success && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out closing the iPhone file.", exception);
        }
        catch when (!success)
        {
            // Cleanup must preserve the original operation failure, including caller cancellation.
        }
    }

    public async Task RenameNewFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        ValidatePath(source);
        ValidatePath(destination);
        if (await this.GetFileInfoAsync(destination, cancellationToken) is not null)
        {
            throw new IOException($"Refusing to replace an iPhone file: {destination}");
        }

        await this.RequestAsync(
            AfcOperation.RenamePath,
            Encoding.UTF8.GetBytes(source + '\0' + destination + '\0'),
            cancellationToken,
            AfcOperation.Status);
    }

    public async Task<IAsyncDisposable> AcquireSyncLockAsync(CancellationToken cancellationToken)
    {
        var handle = await this.OpenFileAsync(SyncPaths.Lock, AfcOpenMode.ReadWrite, cancellationToken);
        try
        {
            var body = new byte[2 * sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(body, handle);
            var flags = AfcLockFlags.Exclusive | AfcLockFlags.NonBlocking;
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(sizeof(ulong)), (ulong)flags);

            await this.RequestAsync(
                AfcOperation.FileLock,
                body,
                cancellationToken,
                AfcOperation.Status);

            return new AfcFileLease(this, handle, cancellationToken);
        }
        catch
        {
            await this.DisposeAsync();
            throw;
        }
    }

    private async Task<ulong> OpenFileAsync(string path, AfcOpenMode mode, CancellationToken cancellationToken)
    {
        var body = new byte[sizeof(ulong) + Encoding.UTF8.GetByteCount(path) + 1];
        BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)mode);
        Encoding.UTF8.GetBytes(path).CopyTo(body, sizeof(ulong));

        var response = await this.RequestAsync(
            AfcOperation.FileOpen,
            body,
            cancellationToken,
            AfcOperation.FileOpenResult);

        return response.Length == sizeof(ulong)
            ? BinaryPrimitives.ReadUInt64LittleEndian(response)
            : throw new InvalidDataException("Invalid AFC file handle.");
    }

    internal async ValueTask CloseFileAsync(ulong handle, CancellationToken cancellationToken)
    {
        var body = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(body, handle);
        await this.RequestAsync(
            AfcOperation.FileClose,
            body,
            cancellationToken,
            AfcOperation.Status);
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathLength)
        {
            throw new InvalidDataException("Unsafe AFC path.");
        }

        var invalidCharacters = path.Contains('\\') || path.Any(char.IsControl);
        var invalidComponents = path.Split('/').Any(part => part.Length == 0 || part is AfcKeys.CurrentDirectory or AfcKeys.ParentDirectory);
        if (invalidCharacters || invalidComponents) throw new InvalidDataException("Unsafe AFC path.");
    }

    internal async Task<string> HashFileAsync(string path, long size, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        await using var sink = new CryptoStream(
            Stream.Null,
            hash,
            CryptoStreamMode.Write,
            leaveOpen: true);

        await this.CopyFileToAsync(
            path,
            sink,
            size,
            cancellationToken);

        await sink.FlushFinalBlockAsync(cancellationToken);
        return Convert.ToHexString(hash.Hash!);
    }

    public static bool SameAttributes(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null) return left is null && right is null;

        // Access times can change because of our own read; all other attributes must remain stable.
        return StableAttributes(left).SequenceEqual(StableAttributes(right), AttributeComparer);
    }

    private static IEnumerable<KeyValuePair<string, string>> StableAttributes(
        IReadOnlyDictionary<string, string> attributes)
    {
        return attributes.Where(p => p.Key != AfcKeys.AccessTime).OrderBy(p => p.Key, StringComparer.Ordinal);
    }

    private async Task<byte[]> RequestAsync(
        AfcOperation operation,
        byte[] body,
        CancellationToken cancellationToken,
        AfcOperation expectedOperation = AfcOperation.Data,
        int? headerBodyBytes = null)
    {
        if (this.IsBroken) throw new IOException("The AFC connection is closed.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var id = ++this.sequence;
        try
        {
            var packet = new byte[HeaderBytes + body.Length];
            Magic.CopyTo(packet);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(TotalLengthOffset), (ulong)packet.Length);
            var thisLength = (ulong)(HeaderBytes + (headerBodyBytes ?? body.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(ThisLengthOffset), thisLength);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(SequenceOffset), id);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(OperationOffset), (ulong)operation);
            body.CopyTo(packet, HeaderBytes);

            await stream.WriteAsync(packet, deadline.Token);
            var header = new byte[HeaderBytes];
            await stream.ReadExactlyAsync(header, deadline.Token);

            var size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(TotalLengthOffset));
            var thisSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(ThisLengthOffset));
            var responseId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(SequenceOffset));
            var responseOperation = (AfcOperation)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(OperationOffset));
            var validMagic = header.AsSpan(0, Magic.Length).SequenceEqual(Magic);
            var validLengths = size is >= HeaderBytes and <= Plist.MaxBytes && thisSize >= HeaderBytes && thisSize <= size;
            if (!validMagic || !validLengths || responseId != id) throw new InvalidDataException("Invalid AFC response header.");

            var response = new byte[checked((int)size - HeaderBytes)];
            await stream.ReadExactlyAsync(response, deadline.Token);
            if (responseOperation == AfcOperation.Status)
            {
                if (response.Length != sizeof(ulong)) throw new InvalidDataException("Invalid AFC status response.");

                var code = BinaryPrimitives.ReadUInt64LittleEndian(response);
                if (code != (ulong)AfcStatusCode.Success) throw new AfcException(code);
                return expectedOperation == AfcOperation.Status
                    ? []
                    : throw new InvalidDataException("Unexpected AFC success status without response data.");
            }

            return responseOperation == expectedOperation
                ? response : throw new InvalidDataException("Unexpected AFC response operation.");
        }
        catch (AfcException)
        {
            throw;
        } // A complete error response preserves the frame boundary.
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await this.DisposeAsync();
            throw new IOException("Timed out waiting for the iPhone file service.", e);
        }
        catch
        {
            await this.DisposeAsync();
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseFields(byte[] bytes)
    {
        if (bytes.Length == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (bytes[^1] != 0) throw new InvalidDataException("Unterminated AFC file information.");

        var fields = Encoding.UTF8.GetString(bytes.AsSpan(0, bytes.Length - 1)).Split('\0');
        if (fields.Length % 2 != 0) throw new InvalidDataException("Malformed AFC file information.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i += 2)
        {
            if (!result.TryAdd(fields[i], fields[i + 1]))
            {
                throw new InvalidDataException("Duplicate AFC information key.");
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.IsBroken) return;
        this.IsBroken = true;
        await stream.DisposeAsync();
    }
}
