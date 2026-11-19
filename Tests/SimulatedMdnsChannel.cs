using System.Net;
using System.Net.Sockets;
using NoTunes.Apple;

namespace NoTunes.Tests;

internal sealed class SimulatedMdnsChannel(params UdpReceiveResult[] responses) : IMdnsChannel
{
    private readonly Queue<UdpReceiveResult> responses = new(responses);

    public long InterfaceIndex => 7;

    public List<byte[]> Sent { get; } = [];

    public Action<byte[]>? OnSend { get; init; }

    public ValueTask SendAsync(byte[] query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Sent.Add(query);
        this.OnSend?.Invoke(query);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.responses.TryDequeue(out var response)) return response;

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }

    public void Dispose()
    {
    }

    internal static UdpReceiveResult Reply(byte[] data, int port = MdnsPacket.Port)
    {
        return new UdpReceiveResult(data, new IPEndPoint(IPAddress.Parse("192.168.1.42"), port));
    }
}
