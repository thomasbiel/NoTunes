using System.Buffers.Binary;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class AfcPeer : NonSeekableTestStream
{
    private const int DefaultReadFragmentBytes = 5;
    private const int DefaultChunkLimit = 1024 * 1024;
    private const ulong InjectedFailureStatus = 10;
    internal const ulong LockWouldBlockStatus = 19;

    public Dictionary<string, byte[]> Files { get; init; } = new(StringComparer.Ordinal);

    public int ReadFragmentBytes { get; init; } = DefaultReadFragmentBytes;

    public long FreeBytes { get; init; } = long.MaxValue;

    public bool AllowWrites { get; init; }

    public bool DeferWrites { get; init; }

    public bool AllowPipelinedFileInfo { get; set; }

    public Dictionary<string, Dictionary<string, string>> FileInfoOverrides { get; } = new(StringComparer.Ordinal);

    public List<AfcOperation> Operations { get; } = [];

    public bool RequireParentDirectories { get; init; }

    public bool FailWrite { get; init; }

    public bool CorruptWrite { get; init; }

    public List<byte[]> WritePackets { get; } = [];

    public HashSet<string> Directories { get; init; } = new(StringComparer.Ordinal);

    public HashSet<string> LockedPaths { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<ulong, (string Path, int Offset)> handles = [];
    private MemoryStream input = new();
    private bool closePending;

    public int ChunkLimit { get; init; } = DefaultChunkLimit;

    public bool EarlyEof { get; init; }

    public bool FailClose { get; init; }

    public bool StallClose { get; init; }

    public bool WasDisposed { get; private set; }

    public Action? OnClose { get; set; }

    public Action<string>? OnFileClosed { get; set; }

    public Action<string>? OnFileInfoRead { get; set; }

    public Func<CancellationToken, Task>? BeforeFileWriteReplyAsync { get; init; }

    public Func<CancellationToken, Task>? BeforeFileReadReplyAsync { get; init; }

    public Action? BeforeTransportRead { get; init; }

    public int Opens { get; private set; }

    public int Closes { get; private set; }

    public int Reads { get; private set; }

    public List<string> ReadPaths { get; } = [];

    public int OpenHandles => this.handles.Count;

    public override bool CanRead => true;

    public override bool CanWrite => true;













    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.input.Read(buffer, offset, Math.Min(count, this.ReadFragmentBytes));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.BeforeTransportRead?.Invoke();
        if (this.closePending)
            await Task.Delay(Timeout.Infinite, cancellationToken);

        return await this.input.ReadAsync(buffer[..Math.Min(buffer.Length, this.ReadFragmentBytes)], cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.DeferWrites) await Task.Yield();

        cancellationToken.ThrowIfCancellationRequested();
        this.Handle(buffer.ToArray());
        if (this.BeforeFileWriteReplyAsync is not null
            && BinaryPrimitives.ReadUInt64LittleEndian(buffer.Span[ProtocolFrames.AfcOperationOffset..])
                == (ulong)AfcOperation.FileWrite)
        {
            await this.BeforeFileWriteReplyAsync(cancellationToken);
        }

        if (this.BeforeFileReadReplyAsync is not null
            && BinaryPrimitives.ReadUInt64LittleEndian(buffer.Span[ProtocolFrames.AfcOperationOffset..])
                == (ulong)AfcOperation.FileRead)
        {
            await this.BeforeFileReadReplyAsync(cancellationToken);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => this.Handle([.. buffer.AsSpan(offset, count)]);

    private void Handle(byte[] request)
    {
        var operation = (AfcOperation)BinaryPrimitives.ReadUInt64LittleEndian(
            request.AsSpan(ProtocolFrames.AfcOperationOffset));

        this.Operations.Add(operation);
        if (!this.AllowPipelinedFileInfo || operation != AfcOperation.GetFileInfo)
        {
            Assert.That(this.input.Position, Is.EqualTo(this.input.Length), "The previous response must be consumed first.");
        }

        var id = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(ProtocolFrames.AfcSequenceOffset));
        var body = request.AsSpan(ProtocolFrames.AfcHeaderBytes);
        switch (operation)
        {
            case AfcOperation.GetDeviceInfo:
                this.Response(id, AfcOperation.Data,
                    Encoding.UTF8.GetBytes($"{AfcKeys.FreeBytes}\0{this.FreeBytes}\0"));

                return;
            case AfcOperation.RemovePath when this.AllowWrites:
                this.Files.Remove(Encoding.UTF8.GetString(body).TrimEnd('\0'));
                this.Status(id, 0);
                return;
            case AfcOperation.ReadDirectory:
                var directory = Encoding.UTF8.GetString(body).TrimEnd('\0');
                var names = this.Files.Keys.Where(name => name.StartsWith(directory + "/", StringComparison.Ordinal))
                    .Select(name => name[(directory.Length + 1)..]).ToArray();

                this.Response(id, AfcOperation.Data,
                    Encoding.UTF8.GetBytes(string.Join('\0', new[] { ".", ".." }.Concat(names)) + '\0'));

                return;
            case AfcOperation.MakeDirectory when this.AllowWrites:
                this.Directories.Add(Encoding.UTF8.GetString(body).TrimEnd('\0'));
                this.Status(id, 0);
                return;
            case AfcOperation.GetFileInfo:
                this.ReadFileInfo(id, body);
                this.OnFileInfoRead?.Invoke(Encoding.UTF8.GetString(body).TrimEnd('\0'));
                return;
            case AfcOperation.FileOpen:
                this.OpenFile(id, body);
                return;
            case AfcOperation.FileRead:
                this.ReadFile(id, body);
                return;
            case AfcOperation.FileWrite when this.AllowWrites:
                this.WriteFile(id, request);
                return;
            case AfcOperation.RenamePath when this.AllowWrites:
                var rename = Encoding.UTF8.GetString(body).TrimEnd('\0').Split('\0');
                Assert.That(this.Files.ContainsKey(rename[1]), Is.False);
                this.Files[rename[1]] = this.Files[rename[0]];
                this.Files.Remove(rename[0]);
                this.Status(id, 0);
                return;
            case AfcOperation.FileLock when this.AllowWrites:
                var lockPath = this.handles[BinaryPrimitives.ReadUInt64LittleEndian(body)].Path;
                this.Status(id, this.LockedPaths.Contains(lockPath) ? LockWouldBlockStatus : 0);
                return;
            case AfcOperation.FileClose:
                this.CloseFile(id, body);
                return;
            default:
                Assert.Fail($"Unexpected AFC operation {operation}; the reader must not modify any files.");
                return;
        }
    }

    private void ReadFileInfo(ulong id, ReadOnlySpan<byte> body)
    {
        var path = Encoding.UTF8.GetString(body).TrimEnd('\0');
        if (this.Directories.Contains(path))
        {
            this.Response(id, AfcOperation.Data, Encoding.UTF8.GetBytes("st_ifmt\0S_IFDIR\0"));
            return;
        }

        if (!this.Files.TryGetValue(path, out var file))
        {
            this.Status(id, (ulong)AfcStatusCode.ObjectNotFound);
            return;
        }

        var attributes = new Dictionary<string, string>
        {
            [AfcKeys.FileType] = "S_IFREG",
            [AfcKeys.FileSize] = file.Length.ToString(),
            [AfcKeys.ModificationTime] = "1"
        };

        if (this.FileInfoOverrides.TryGetValue(path, out var overrides))
        {
            foreach (var pair in overrides)
            {
                attributes[pair.Key] = pair.Value;
            }
        }

        this.Response(id, AfcOperation.Data,
            Encoding.UTF8.GetBytes(string.Join('\0', attributes.SelectMany(pair => new[] { pair.Key, pair.Value })) + '\0'));
    }

    private void OpenFile(ulong id, ReadOnlySpan<byte> body)
    {
        var mode = (AfcOpenMode)BinaryPrimitives.ReadUInt64LittleEndian(body);
        Assert.That(mode == AfcOpenMode.ReadOnly
            || this.AllowWrites && mode is AfcOpenMode.ReadWrite or AfcOpenMode.WriteTruncate,
            Is.True, "Only explicitly enabled writes are allowed.");

        var name = Encoding.UTF8.GetString(body[sizeof(ulong)..]).TrimEnd('\0');
        var separator = name.LastIndexOf('/');
        if (this.RequireParentDirectories && mode != AfcOpenMode.ReadOnly && separator >= 0
            && !this.Directories.Contains(name[..separator]))
        {
            this.Status(id, (ulong)AfcStatusCode.ObjectNotFound);
            return;
        }

        if (mode == AfcOpenMode.WriteTruncate || mode == AfcOpenMode.ReadWrite && !this.Files.ContainsKey(name))
            this.Files[name] = [];

        Assert.That(this.Files.ContainsKey(name), Is.True);
        var handle = (ulong)++this.Opens;
        this.handles.Add(handle, (name, 0));
        var opened = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(opened, handle);
        this.Response(id, AfcOperation.FileOpenResult, opened);
    }

    private void ReadFile(ulong id, ReadOnlySpan<byte> body)
    {
        this.Reads++;
        var readHandle = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var requested = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(body[sizeof(ulong)..]));
        var source = this.handles[readHandle];
        this.ReadPaths.Add(source.Path);
        var count = this.EarlyEof ? 0 : Math.Min(Math.Min(this.ChunkLimit, requested), this.Files[source.Path].Length - source.Offset);
        this.Response(id, AfcOperation.Data, [.. this.Files[source.Path].AsSpan(source.Offset, count)]);
        this.handles[readHandle] = (source.Path, source.Offset + count);
    }

    private void WriteFile(ulong id, byte[] request)
    {
        var body = request.AsSpan(ProtocolFrames.AfcHeaderBytes);
        this.WritePackets.Add(request);
        if (this.FailWrite)
        {
            this.Status(id, InjectedFailureStatus);
            return;
        }

        var target = this.handles[BinaryPrimitives.ReadUInt64LittleEndian(body)];
        var content = body[sizeof(ulong)..].ToArray();
        if (this.CorruptWrite && content.Length > 0)
            content[0] ^= 1;
        this.Files[target.Path] = [.. this.Files[target.Path], .. content];
        this.Status(id, 0);
    }

    private void CloseFile(ulong id, ReadOnlySpan<byte> body)
    {
        this.Closes++;
        Assert.That(this.handles.Remove(BinaryPrimitives.ReadUInt64LittleEndian(body), out var closed), Is.True);
        this.OnClose?.Invoke();
        this.OnFileClosed?.Invoke(closed.Path);
        if (this.StallClose)
        {
            this.closePending = true;
            return;
        }

        this.Status(id, this.FailClose ? InjectedFailureStatus : (ulong)AfcStatusCode.Success);
    }

    private void Status(ulong id, ulong code)
    {
        var body = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(body, code);
        this.Response(id, AfcOperation.Status, body);
    }

    private void Response(ulong id, AfcOperation operation, byte[] body)
    {
        byte[] remaining = this.input.Position == this.input.Length
            ? []
            : this.input.ToArray().AsSpan((int)this.input.Position).ToArray();

        this.input.Dispose();
        this.input = new MemoryStream([.. remaining, .. ProtocolFrames.Afc(id, (ulong)operation, body)], writable: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.WasDisposed = true;
            this.input.Dispose();
        }

        base.Dispose(disposing);
    }
}
