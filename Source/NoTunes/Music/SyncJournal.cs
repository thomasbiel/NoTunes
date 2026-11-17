using System.Security.Cryptography;
using System.Text.Json;
using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class SyncJournal : IDisposable
{
    private const string PendingFileName = "pending.json";
    private const string LockFileName = "sync.lock";
    private const string VerifiedPrefix = "verified-";
    private const string UnappliedPrefix = "unapplied-";
    private const string ReconciledPrefix = "reconciled-";
    private const string AssetsPrefix = "assets-";
    private const string JournalExtension = ".json";
    private const int FormatVersion = 1;
    private const int RecoverableFormatVersion = 2;
    private const long MaximumJournalBytes = 256L * 1024 * 1024;
    private readonly FileStream lease;
    private readonly string pending;

    internal string DirectoryPath { get; }

    internal bool HasPending => File.Exists(this.pending);

    public SyncJournal(string root, string device, bool allowPending = false)
    {
        var directory = DeviceState.DirectoryFor(root, device);
        this.DirectoryPath = directory;
        this.pending = Path.Combine(directory, PendingFileName);
        this.lease = new FileStream(
            Path.Combine(directory, LockFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        if (this.HasPending && !allowPending)
        {
            this.lease.Dispose();
            throw new IOException($"An earlier sync has an unverified outcome. Inspect {this.pending} and run --dry-run before recovery. No operations were replayed.");
        }
    }

    public PendingSync ReadPending()
    {
        using var file = File.OpenRead(this.pending);
        if (file.Length > MaximumJournalBytes) throw new InvalidDataException("The recovery journal exceeds its size limit.");

        var state = JsonSerializer.Deserialize<PendingSync>(file);
        if (state is null || state.Version != RecoverableFormatVersion || state.Before is null || state.Identity is null
            || state.Operation is null || state.Operation.Length is < 1 or > SyncProtocol.MaxOperationBytes
            || state.Signature is null || state.Additions is null || state.Deletions is null)
            throw new IOException("This journal cannot be recovered automatically; retain it for manual reconciliation.");

        return state;
    }

    public string Assets(PendingSync state)
    {
        return Path.Combine(this.DirectoryPath, AssetsPrefix + Convert.ToHexString(SHA256.HashData(state.Operation)));
    }

    public async Task StartBatchAsync(
        LibraryIdentity identity,
        DeviceCatalogue before,
        SyncBatch batch,
        byte[] signature,
        CancellationToken cancellationToken,
        bool replacePending = false)
    {
        if (this.HasPending && !replacePending) throw new IOException("An operation still requires recovery.");

        var previous = this.HasPending ? this.ReadPending() : null;
        var state = new PendingSync(
            RecoverableFormatVersion,
            identity,
            CatalogueCheckpoint.Capture(before),
            [.. batch.Additions.Select(JournalTrack.Capture)],
            [.. batch.Deletions],
            batch.Operation,
            signature);
        var directory = this.Assets(state);
        try
        {
            Directory.CreateDirectory(directory);
            await CopyAssetsAsync(batch, directory, cancellationToken);
            DeviceState.Save(this.pending, state, MaximumJournalBytes);
        }
        catch
        {
            if (previous is null || !StringComparer.Ordinal.Equals(this.Assets(previous), directory))
            {
                this.CleanAssets(state);
            }

            throw;
        }

        if (previous is not null && !StringComparer.Ordinal.Equals(this.Assets(previous), directory))
        {
            this.CleanAssets(previous);
        }
    }

    private static async Task CopyAssetsAsync(SyncBatch batch, string directory, CancellationToken cancellationToken)
    {
        foreach (var track in batch.Additions)
        {
            var path = JournalTrack.AssetPath(directory, track.Id, SyncPaths.Mp3Extension);
            await using (var input = File.OpenRead(track.LocalPath))
            await using (var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            await using var copy = File.OpenRead(path);
            if (copy.Length != track.Source.Size || Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken))
                != track.Source.Sha256) throw new IOException("Durable recovery audio failed verification.");

            if (track.Metadata.Artwork is { } artwork)
            {
                await using var cover = new FileStream(
                    JournalTrack.AssetPath(directory, track.Id, SyncPaths.JpegExtension),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                cover.Write(artwork.Bytes);
                cover.Flush(flushToDisk: true);
            }
        }
    }

    public void ResolveUnapplied()
    {
        var state = this.ReadPending();
        File.Move(this.pending, Path.Combine(this.DirectoryPath, UnappliedPrefix + Guid.NewGuid().ToString("N") + JournalExtension));
        this.CleanAssets(state);
    }

    public void ResolveObserved()
    {
        var state = this.ReadPending();
        File.Move(this.pending, Path.Combine(this.DirectoryPath, ReconciledPrefix + Guid.NewGuid().ToString("N") + JournalExtension));
        this.CleanAssets(state);
    }

    public void Start(LibraryIdentity identity, SyncPlan plan, byte[] operation, byte[] signature)
    {
        using var file = new FileStream(
            this.pending,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(file, new
        {
            Version = FormatVersion,
            CreatedUtc = DateTime.UtcNow,
            identity.Binding,
            Plan = plan,
            Operation = operation,
            Signature = signature
        });
        file.Flush(flushToDisk: true);
    }

    public void Complete()
    {
        PendingSync? state = null;
        try
        {
            state = this.ReadPending();
        }
        catch (IOException)
        {
            // Explicit completion of a legacy record remains available to verified callers.
        }

        File.Move(this.pending, Path.Combine(Path.GetDirectoryName(this.pending)!, VerifiedPrefix + Guid.NewGuid().ToString("N") + JournalExtension));
        if (state is not null) this.CleanAssets(state);
    }

    private void CleanAssets(PendingSync state)
    {
        var directory = this.Assets(state);
        try
        {
            foreach (var track in state.Additions)
            {
                File.Delete(JournalTrack.AssetPath(directory, track.Id, SyncPaths.Mp3Extension));
                File.Delete(JournalTrack.AssetPath(directory, track.Id, SyncPaths.JpegExtension));
            }

            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide a preparation failure or undo a settled operation.
        }
    }

    public void Dispose() => this.lease.Dispose();
}
