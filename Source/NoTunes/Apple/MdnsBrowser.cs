using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NoTunes.Apple;

internal static class MdnsBrowser
{
    private const int MaximumPacketsPerChannel = 256;
    private const int MaximumQueriesPerChannel = 256;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    internal static async Task<IReadOnlyList<IPEndPoint>> BrowseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channels = MdnsSocket.Open();
        try
        {
            if (channels.Count == 0)
            {
                throw new IOException("No multicast-capable network interface is available for Bonjour.");
            }

            var results = await Task.WhenAll(
                channels.Select(channel => BrowseAsync(channel, timeout, cancellationToken)));

            return [.. results.SelectMany(endpoints => endpoints).Distinct()];
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    internal static async Task<IReadOnlyList<IPEndPoint>> BrowseAsync(
        IMdnsChannel channel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var records = new BonjourRecords();
        var sent = new HashSet<MdnsQuestion>();
        var queryCount = 0;
        var retryAt = DateTimeOffset.MinValue;
        try
        {
            for (var packet = 0; packet < MaximumPacketsPerChannel; packet++)
            {
                if (DateTimeOffset.UtcNow >= retryAt)
                {
                    sent.Clear();
                    retryAt = DateTimeOffset.UtcNow + RetryInterval;
                }

                foreach (var question in records.Questions())
                {
                    if (sent.Add(question) && queryCount < MaximumQueriesPerChannel)
                    {
                        await channel.SendAsync(MdnsPacket.Query(question), deadline.Token);
                        queryCount++;
                    }
                }

                using var receive = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var remaining = retryAt - DateTimeOffset.UtcNow;
                receive.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

                UdpReceiveResult response;
                try
                {
                    response = await channel.ReceiveAsync(receive.Token);
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    continue;
                }

                if (response.RemoteEndPoint.Port != MdnsPacket.Port) continue;

                try
                {
                    records.Add(new MdnsPacket(response.Buffer).Read(channel.InterfaceIndex));
                }
                catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
                {
                    // Ignore malformed datagrams from unrelated or broken responders.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The bounded browsing window finished; return all complete service answers collected so far.
        }
        catch (SocketException)
        {
            // Other interfaces can still return usable results.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return records.Endpoints();
    }
}
