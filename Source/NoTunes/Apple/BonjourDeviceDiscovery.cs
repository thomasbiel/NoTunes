using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NoTunes.Apple;

internal sealed class BonjourDeviceDiscovery(UsbMuxClient mux, TimeSpan timeout)
{
    private const int MaximumCandidates = 64;
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(3);

    internal Func<TimeSpan, CancellationToken, Task<IReadOnlyList<IPEndPoint>>> Browse { get; init; } =
        MdnsBrowser.BrowseAsync;

    internal Func<UsbDevice, CancellationToken, Task>? Authenticate { get; init; }

    internal async Task<IReadOnlyList<UsbDevice>> FindAsync(string udid, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Reserve a separate, bounded window for authenticating the discovered addresses.
        deadline.CancelAfter(timeout + BrowseTimeout);
        var browseTime = timeout < BrowseTimeout ? timeout : BrowseTimeout;
        IReadOnlyList<IPEndPoint> endpoints;
        try
        {
            endpoints = await this.Browse(browseTime, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Bonjour discovery timed out.", exception);
        }

        var candidates = endpoints
            // Modern mobdev2 adverts can point at a remote-pairing port; lockdown still uses its own fixed port.
            .Where(endpoint => endpoint.Port != 0
                && DeviceNetworkAddress.IsUnicast(endpoint.Address) && !IPAddress.IsLoopback(endpoint.Address))
            .DistinctBy(endpoint => endpoint.Address)
            .OrderBy(endpoint => endpoint.AddressFamily != AddressFamily.InterNetwork)
            .Take(MaximumCandidates)
            .ToArray();

        try
        {
            foreach (var endpoint in candidates)
            {
                var device = new UsbDevice(0, DeviceUdid.Format(udid), 0)
                {
                    NetworkAddress = endpoint.Address.ToString()
                };

                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(timeout < CandidateTimeout ? timeout : CandidateTimeout);
                try
                {
                    await (this.Authenticate ?? this.AuthenticateAsync)(device, attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    return [device];
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    // A stale address must not prevent trying the next advertised address.
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or AuthenticationException)
                {
                    // A Bonjour name or MAC is not proof of identity; require pinned TLS and a matching UDID.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Authentication exhausted its overall time budget.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Length != 0)
        {
            throw new IOException($"Bonjour found {candidates.Length} iPhone service address(es), but none could be "
                + $"authenticated as UDID '{udid}'. Unlock the paired iPhone and check its Wi-Fi sync setting.");
        }

        return [];
    }

    private async Task AuthenticateAsync(UsbDevice device, CancellationToken cancellationToken)
    {
        await using var lockdown = await LockdownClient.ConnectAsync(
            mux,
            device,
            timeout,
            cancellationToken);

        await lockdown.StartPairedSessionAsync(cancellationToken);
        await lockdown.GetInfoAsync(cancellationToken);
    }
}
