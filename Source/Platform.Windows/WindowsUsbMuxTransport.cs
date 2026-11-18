using System.Net;
using System.Net.Sockets;

namespace NoTunes.Platform.Windows;

public sealed class WindowsUsbMuxTransport : IUsbMuxTransport
{
    public const int DefaultPort = 27015;
    private readonly IPEndPoint endpoint;

    public WindowsUsbMuxTransport(IPEndPoint? endpoint = null)
    {
        this.endpoint = endpoint ?? new IPEndPoint(IPAddress.Loopback, DefaultPort);
        if (!IPAddress.IsLoopback(this.endpoint.Address))
        {
            throw new ArgumentException("The USB multiplexer endpoint must be a loopback address.", nameof(endpoint));
        }
    }

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(this.endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(this.endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new IOException($"Cannot connect to Apple Mobile Device Service ({this.endpoint}). "
                                  + "Install Apple device support, start its service and reconnect the unlocked iPhone over USB.", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
