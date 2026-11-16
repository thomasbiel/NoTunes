using System.Buffers.Binary;
using System.Text;
using System.Globalization;
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

    private static ReadOnlySpan<byte> Magic => "CFA6LPAA"u8;

    private ulong sequence;
    private bool broken;

    internal bool IsBroken => this.broken;

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        return ParseFields(await this.RequestAsync(AfcOperation.GetDeviceInfo, [], cancellationToken));
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return ParseFields(await this.RequestAsync(AfcOperation.GetFileInfo, Encoding.UTF8.GetBytes(path + '\0'), cancellationToken));
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
        var before = await this.GetFileInfoAsync(path, cancellationToken) ?? throw new FileNotFoundException($"iPhone file not found: {path}");
        if (before.GetValueOrDefault(AfcKeys.FileType) != RegularFileType || !long.TryParse(
            before.GetValueOrDefault(AfcKeys.FileSize),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var size) || size < 0 || size > maxBytes)
            throw new IOException($"iPhone file is not a regular file within the {maxBytes}-byte read limit: {path}");

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
                    throw new InvalidDataException($"Unexpected AFC file read length: {path}");

                await output.WriteAsync(chunk, cancellationToken);
                offset += chunk.Length;
            }

            success = true;
        }
        finally
        {
            // Even a locally cancelled read must not leave a file handle open on a usable connection.
            if (!this.broken)
            {
                using var closeDeadline = new CancellationTokenSource(CloseTimeout);
                try
                {
                    await this.CloseFileAsync(handle, closeDeadline.Token);
                }
                catch (OperationCanceledException e) when (success && closeDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Timed out closing the iPhone file.", e);
                }
                catch when (!success)
                { /* Keep the original failure; the close path discards a broken socket. */
                }
            }
        }

        var after = await this.GetFileInfoAsync(path, cancellationToken);
        if (!SameAttributes(before, after)) throw new IOException($"iPhone file changed while being read: {path}");

    }

    public async Task<IReadOnlyList<string>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var bytes = await this.RequestAsync(AfcOperation.ReadDirectory, Encoding.UTF8.GetBytes(path + '\0'), cancellationToken);
        if (bytes.Length == 0 || bytes[^1] != 0) throw new InvalidDataException("Invalid AFC directory listing.");

        return
        [
            .. Encoding.UTF8.GetString(bytes.AsSpan(0, bytes.Length - 1)).Split('\0')
                .Where(name => name.Length != 0 && name is not AfcKeys.CurrentDirectory and not AfcKeys.ParentDirectory)
        ];
    }

    public async Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var info = await this.GetFileInfoAsync(path, cancellationToken);
        if (info is not null)
        {
            if (info.GetValueOrDefault(AfcKeys.FileType) != AfcKeys.DirectoryType)
                throw new IOException($"Expected an iPhone directory: {path}");

            return;
        }

        var slash = path.LastIndexOf('/');
        if (slash > 0)
            await this.EnsureDirectoryAsync(path[..slash], cancellationToken);
        await this.RequestAsync(
            AfcOperation.MakeDirectory,
            Encoding.UTF8.GetBytes(path + '\0'),
            cancellationToken,
            AfcOperation.Status);
    }

    public async Task WriteNewFileAsync(string path, Stream input, long size, CancellationToken cancellationToken,
        Func<long, CancellationToken, Task>? progress = null)
    {
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (await this.GetFileInfoAsync(path, cancellationToken) is not null)
            throw new IOException($"Refusing to overwrite an existing iPhone file: {path}");

        var handle = await this.OpenFileAsync(path, AfcOpenMode.WriteTruncate, cancellationToken);
        var success = false;
        try
        {
            var buffer = new byte[sizeof(ulong) + MaxReadChunkBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, handle);
            long remaining = size;
            while (remaining > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(sizeof(ulong), (int)Math.Min(remaining, MaxReadChunkBytes)), cancellationToken);
                if (count == 0) throw new IOException("Source ended before the declared transfer size.");

                await this.RequestAsync(
                    AfcOperation.FileWrite,
                    buffer[..(sizeof(ulong) + count)],
                    cancellationToken,
                    AfcOperation.Status,
                    sizeof(ulong));
                remaining -= count;
                if (progress is not null)
                    await progress(size - remaining, cancellationToken);
            }

            if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new IOException("Source exceeds the declared transfer size.");

            success = true;
        }
        finally
        {
            if (!this.broken)
            {
                using var deadline = new CancellationTokenSource(CloseTimeout);
                try
                {
                    await this.CloseFileAsync(handle, deadline.Token);
                }
                catch (OperationCanceledException e) when (
                    success && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Timed out closing the iPhone file.", e);
                }
                catch when (!success)
                {
                    // Preserve the transfer failure; a broken socket cannot be reused.
                }
            }
        }
    }

    public async Task RenameNewFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        ValidatePath(source);
        ValidatePath(destination);
        if (await this.GetFileInfoAsync(destination, cancellationToken) is not null)
            throw new IOException($"Refusing to replace an iPhone file: {destination}");

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
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(sizeof(ulong)), (ulong)(AfcLockFlags.Exclusive | AfcLockFlags.NonBlocking));
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
        if (response.Length != sizeof(ulong)) throw new InvalidDataException("Invalid AFC file handle.");

        return BinaryPrimitives.ReadUInt64LittleEndian(response);
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
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathLength || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Any(part => part.Length == 0 || part is AfcKeys.CurrentDirectory or AfcKeys.ParentDirectory))
            throw new InvalidDataException("Unsafe AFC path.");
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

    public static bool SameAttributes(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        // Access times can change because of our own read; all other attributes must remain stable.
        return StableAttributes(left).SequenceEqual(StableAttributes(right));
    }

    private static IEnumerable<KeyValuePair<string, string>> StableAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        return attributes.Where(p => p.Key != AfcKeys.AccessTime).OrderBy(p => p.Key, StringComparer.Ordinal);
    }

    private async Task<byte[]> RequestAsync(AfcOperation operation, byte[] body, CancellationToken cancellationToken, AfcOperation expectedOperation = AfcOperation.Data, int? headerBodyBytes = null)
    {
        if (this.broken) throw new IOException("The AFC connection is closed.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var id = ++this.sequence;
        try
        {
            var packet = new byte[HeaderBytes + body.Length];
            Magic.CopyTo(packet);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(TotalLengthOffset), (ulong)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(ThisLengthOffset), (ulong)(HeaderBytes + (headerBodyBytes ?? body.Length)));
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
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || size < HeaderBytes || size > Plist.MaxBytes || thisSize < HeaderBytes || thisSize > size || responseId != id)
                throw new InvalidDataException("Invalid AFC response header.");

            var response = new byte[checked((int)size - HeaderBytes)];
            await stream.ReadExactlyAsync(response, deadline.Token);
            if (responseOperation == AfcOperation.Status)
            {
                if (response.Length != sizeof(ulong)) throw new InvalidDataException("Invalid AFC status response.");

                var code = BinaryPrimitives.ReadUInt64LittleEndian(response);
                if (code != (ulong)AfcStatusCode.Success) throw new AfcException(code);
                if (expectedOperation != AfcOperation.Status)
                    throw new InvalidDataException("Unexpected AFC success status without response data.");

                return [];
            }

            if (responseOperation != expectedOperation)
                throw new InvalidDataException("Unexpected AFC response operation.");

            return response;
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
        if (bytes.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        if (bytes[^1] != 0) throw new InvalidDataException("Unterminated AFC file information.");

        var fields = Encoding.UTF8.GetString(bytes.AsSpan(0, bytes.Length - 1)).Split('\0');
        if (fields.Length % 2 != 0) throw new InvalidDataException("Malformed AFC file information.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i += 2)
        {
            if (!result.TryAdd(fields[i], fields[i + 1]))
                throw new InvalidDataException("Duplicate AFC information key.");
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.broken)
            return;
        this.broken = true;
        await stream.DisposeAsync();
    }
}
