using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class SyncJournal : IDisposable
{
    private const string PendingFileName = "pending.json";
    private const string LockFileName = "sync.lock";
    private const string VerifiedPrefix = "verified-";
    private const string JournalExtension = ".json";
    private const int FormatVersion = 1;
    private readonly FileStream lease;
    private readonly string pending;

    public SyncJournal(string root, string device)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceUdid.Normalize(device).ToUpperInvariant())));
        var directory = Path.Combine(root, key);
        Directory.CreateDirectory(directory);
        this.pending = Path.Combine(directory, PendingFileName);
        this.lease = new FileStream(
            Path.Combine(directory, LockFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        if (File.Exists(this.pending))
        {
            this.lease.Dispose();
            throw new IOException($"An earlier sync has an unverified outcome. Inspect {this.pending} and run --dry-run before recovery. No operations were replayed.");
        }
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
        File.Move(this.pending, Path.Combine(Path.GetDirectoryName(this.pending)!, VerifiedPrefix + Guid.NewGuid().ToString("N") + JournalExtension));
    }

    public void Dispose() => this.lease.Dispose();
}
