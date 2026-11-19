using System.Net.Sockets;

namespace NoTunes.Apple;

internal interface IMdnsChannel : IDisposable
{
    long InterfaceIndex { get; }

    ValueTask SendAsync(byte[] query, CancellationToken cancellationToken);

    ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}
