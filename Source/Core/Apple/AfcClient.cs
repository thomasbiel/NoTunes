using System.Buffers.Binary;
using System.Text;
using System.Globalization;

namespace NoTunes.Apple;

// Read-only AFC primitives. In particular, this class cannot overwrite the phone's database.
public sealed class AfcClient(Stream stream, TimeSpan timeout) : IAsyncDisposable
{
    private const ulong ReadOnlyOpenMode = 1;
    private const int HeaderBytes = 5 * sizeof(ulong);
    private const int TotalLengthOffset = sizeof(ulong);
    private const int ThisLengthOffset = 2 * sizeof(ulong);
    private const int SequenceOffset = 3 * sizeof(ulong);
    private const int OperationOffset = 4 * sizeof(ulong);
    private const int MaxReadChunkBytes = 1024 * 1024;
    private const string RegularFileType = "S_IFREG";
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);
    private static ReadOnlySpan<byte> Magic => "CFA6LPAA"u8;

    private static class Keys
    {
        public const string FileType = "st_ifmt";
        public const string FileSize = "st_size";
        public const string AccessTime = "st_atime";
    }

    private ulong sequence;
    private bool broken;

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
        ParseFields(await RequestAsync(AfcOperation.GetDeviceInfo, [], cancellationToken));

    public async Task<IReadOnlyDictionary<string, string>?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        try { return ParseFields(await RequestAsync(AfcOperation.GetFileInfo, Encoding.UTF8.GetBytes(path + '\0'), cancellationToken)); }
        catch (AfcException e) when (e.Code == (ulong)AfcStatusCode.ObjectNotFound) { return null; }
    }

    public async Task<byte[]> ReadFileAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var before = await GetFileInfoAsync(path, cancellationToken)
            ?? throw new FileNotFoundException($"iPhone file not found: {path}");
        if (before.GetValueOrDefault(Keys.FileType) != RegularFileType
            || !long.TryParse(before.GetValueOrDefault(Keys.FileSize), NumberStyles.None,
                CultureInfo.InvariantCulture, out var size) || size < 0 || size > maxBytes)
            throw new IOException($"iPhone file is not a regular file within the {maxBytes}-byte read limit: {path}");
        var open = new byte[sizeof(ulong) + Encoding.UTF8.GetByteCount(path) + 1];
        BinaryPrimitives.WriteUInt64LittleEndian(open, ReadOnlyOpenMode);
        Encoding.UTF8.GetBytes(path).CopyTo(open, sizeof(ulong));
        var opened = await RequestAsync(AfcOperation.FileOpen, open, cancellationToken, expectedOperation: AfcOperation.FileOpenResult);
        if (opened.Length != sizeof(ulong)) throw new InvalidDataException("Invalid AFC file handle.");
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(opened);
        var success = false;
        byte[] data;
        try
        {
            data = new byte[(int)size];
            var offset = 0;
            while (offset < data.Length)
            {
                var count = Math.Min(MaxReadChunkBytes, data.Length - offset);
                var read = new byte[2 * sizeof(ulong)];
                BinaryPrimitives.WriteUInt64LittleEndian(read, handle);
                BinaryPrimitives.WriteUInt64LittleEndian(read.AsSpan(sizeof(ulong)), (ulong)count);
                var chunk = await RequestAsync(AfcOperation.FileRead, read, cancellationToken);
                if (chunk.Length == 0 || chunk.Length > count)
                    throw new InvalidDataException($"Unexpected AFC file read length: {path}");
                chunk.CopyTo(data, offset);
                offset += chunk.Length;
            }
            success = true;
        }
        finally
        {
            // Even a locally cancelled read must not leave a file handle open on a usable connection.
            if (!broken)
            {
                using var closeDeadline = new CancellationTokenSource(CloseTimeout);
                var close = new byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64LittleEndian(close, handle);
                try { await RequestAsync(AfcOperation.FileClose, close, closeDeadline.Token, expectedOperation: AfcOperation.Status); }
                catch (OperationCanceledException e) when (success && closeDeadline.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Timed out closing the iPhone file.", e);
                }
                catch when (!success) { /* Keep the original failure; the close path discards a broken socket. */ }
            }
        }
        var after = await GetFileInfoAsync(path, cancellationToken);
        if (!SameAttributes(before, after)) throw new IOException($"iPhone file changed while being read: {path}");
        return data;
    }

    public static bool SameAttributes(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        // Access times can change because of our own read; all other attributes must remain stable.
        return left.Where(p => p.Key != Keys.AccessTime).OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual(right.Where(p => p.Key != Keys.AccessTime).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    private async Task<byte[]> RequestAsync(AfcOperation operation, byte[] body, CancellationToken cancellationToken,
        AfcOperation expectedOperation = AfcOperation.Data)
    {
        if (broken) throw new IOException("The AFC connection is closed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var id = ++sequence;
        try
        {
            var packet = new byte[HeaderBytes + body.Length];
            Magic.CopyTo(packet);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(TotalLengthOffset), (ulong)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(ThisLengthOffset), (ulong)packet.Length);
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
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || size < HeaderBytes || size > Plist.MaxBytes
                || thisSize < HeaderBytes || thisSize > size || responseId != id)
                throw new InvalidDataException("Invalid AFC response header.");
            var response = new byte[checked((int)size - HeaderBytes)];
            await stream.ReadExactlyAsync(response, deadline.Token);
            if (responseOperation == AfcOperation.Status)
            {
                if (response.Length != sizeof(ulong)) throw new InvalidDataException("Invalid AFC status response.");
                var code = BinaryPrimitives.ReadUInt64LittleEndian(response);
                if (code != (ulong)AfcStatusCode.Success) throw new AfcException(code);
                if (expectedOperation != AfcOperation.Status) throw new InvalidDataException("Unexpected AFC success status without response data.");
                return [];
            }
            if (responseOperation != expectedOperation) throw new InvalidDataException("Unexpected AFC response operation.");
            return response;
        }
        catch (AfcException) { throw; } // A complete error response preserves the frame boundary.
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync();
            throw new IOException("Timed out waiting for the iPhone file service.", e);
        }
        catch { await DisposeAsync(); throw; }
    }

    private static IReadOnlyDictionary<string, string> ParseFields(byte[] bytes)
    {
        var fields = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Split('\0');
        if (fields.Length % 2 != 0) throw new InvalidDataException("Malformed AFC file information.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i += 2)
            if (!result.TryAdd(fields[i], fields[i + 1])) throw new InvalidDataException("Duplicate AFC information key.");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (broken) return;
        broken = true;
        await stream.DisposeAsync();
    }
}

public sealed class AfcException(ulong code) : IOException($"The iPhone file service returned AFC error {code}.")
{
    public ulong Code { get; } = code;
}
