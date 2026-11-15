using System.Buffers.Binary;
using System.Text;
using System.Globalization;

namespace NoTunes.Apple;

// Read-only AFC primitives. In particular, this class cannot overwrite the phone's database.
public sealed class AfcClient(Stream stream, TimeSpan timeout) : IAsyncDisposable
{
    private ulong sequence;
    private bool broken;

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
        ParseFields(await RequestAsync(11, [], cancellationToken));

    public async Task<IReadOnlyDictionary<string, string>?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        try { return ParseFields(await RequestAsync(10, Encoding.UTF8.GetBytes(path + '\0'), cancellationToken)); }
        catch (AfcException e) when (e.Code == 8) { return null; }
    }

    public async Task<byte[]> ReadFileAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var before = await GetFileInfoAsync(path, cancellationToken)
            ?? throw new FileNotFoundException($"iPhone file not found: {path}");
        if (before.GetValueOrDefault("st_ifmt") != "S_IFREG"
            || !long.TryParse(before.GetValueOrDefault("st_size"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var size) || size < 0 || size > maxBytes)
            throw new IOException($"iPhone file is not a regular file within the {maxBytes}-byte read limit: {path}");
        var open = new byte[8 + Encoding.UTF8.GetByteCount(path) + 1];
        BinaryPrimitives.WriteUInt64LittleEndian(open, 1); // AFC_FOPEN_RDONLY
        Encoding.UTF8.GetBytes(path).CopyTo(open, 8);
        var opened = await RequestAsync(13, open, cancellationToken, expectedOperation: 14);
        if (opened.Length != 8) throw new InvalidDataException("Invalid AFC file handle.");
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(opened);
        var success = false;
        byte[] data;
        try
        {
            data = new byte[(int)size];
            var offset = 0;
            while (offset < data.Length)
            {
                var count = Math.Min(1024 * 1024, data.Length - offset);
                var read = new byte[16];
                BinaryPrimitives.WriteUInt64LittleEndian(read, handle);
                BinaryPrimitives.WriteUInt64LittleEndian(read.AsSpan(8), (ulong)count);
                var chunk = await RequestAsync(15, read, cancellationToken);
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
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var close = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(close, handle);
                try { await RequestAsync(20, close, closeDeadline.Token, expectedOperation: 1); }
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
        return left.Where(p => p.Key != "st_atime").OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual(right.Where(p => p.Key != "st_atime").OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    private async Task<byte[]> RequestAsync(ulong operation, byte[] body, CancellationToken cancellationToken,
        ulong expectedOperation = 2)
    {
        if (broken) throw new IOException("The AFC connection is closed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var id = ++sequence;
        try
        {
            var packet = new byte[40 + body.Length];
            "CFA6LPAA"u8.CopyTo(packet);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), (ulong)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), id);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(32), operation);
            body.CopyTo(packet, 40);
            await stream.WriteAsync(packet, deadline.Token);
            var header = new byte[40];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            var thisSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
            var responseId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
            var responseOperation = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
            if (!header.AsSpan(0, 8).SequenceEqual("CFA6LPAA"u8) || size < 40 || size > Plist.MaxBytes
                || thisSize < 40 || thisSize > size || responseId != id)
                throw new InvalidDataException("Invalid AFC response header.");
            var response = new byte[checked((int)size - 40)];
            await stream.ReadExactlyAsync(response, deadline.Token);
            if (responseOperation == 1)
            {
                if (response.Length != 8) throw new InvalidDataException("Invalid AFC status response.");
                var code = BinaryPrimitives.ReadUInt64LittleEndian(response);
                if (code != 0) throw new AfcException(code);
                if (expectedOperation != 1) throw new InvalidDataException("Unexpected AFC success status without response data.");
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
