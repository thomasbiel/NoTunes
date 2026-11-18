using NoTunes.Apple;
using NoTunes.Music;

namespace NoTunes.Tests;

internal sealed class AtcSyncTestSession : IAsyncDisposable
{
    private const uint InitialAnchor = 7;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    private static readonly LibraryIdentity Identity = new("0011223344556677", "binding");
    private readonly AfcClient afc;
    private readonly AtcClient client;
    private readonly AtcSyncSession session;
    private GrappaSigner? signer;

    public AtcSyncTestSession(
        AtcPeer peer,
        AfcPeer? files = null,
        TimeSpan? atcTimeout = null,
        TimeSpan? afcTimeout = null,
        TextWriter? error = null)
    {
        this.afc = new AfcClient(files ?? new AfcPeer { AllowWrites = true }, afcTimeout ?? DefaultTimeout);
        this.client = new AtcClient(peer, atcTimeout ?? DefaultTimeout);
        this.session = new AtcSyncSession(this.client, error ?? TextWriter.Null);
    }

    public async Task BeginAsync()
    {
        await this.client.ReadSyncStatusAsync(CancellationToken.None);
        this.signer = await this.session.BeginAsync(Identity, InitialAnchor, CancellationToken.None);
    }

    public async Task StartAsync()
    {
        await this.BeginAsync();
        await this.session.FinishMetadataAsync(InitialAnchor, CancellationToken.None);
    }

    public Task TransferAsync(IReadOnlyList<PreparedTrack> additions, CancellationToken cancellationToken = default)
    {
        return this.session.TransferAsync(
            this.afc,
            additions,
            new HashSet<long>(),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        this.signer?.Dispose();

        try
        {
            await this.client.DisposeAsync();
        }
        finally
        {
            await this.afc.DisposeAsync();
        }
    }
}
