using System.Net.Sockets;

namespace NoTunes.Platform.Linux;

public sealed class LinuxUsbMuxTransport : IUsbMuxTransport
{
    public const string DefaultSocketPath = "/var/run/usbmuxd";
    private readonly UnixDomainSocketEndPoint endpoint;

    public LinuxUsbMuxTransport(string socketPath = DefaultSocketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!socketPath.StartsWith('/') && !Path.IsPathFullyQualified(socketPath))
        {
            throw new ArgumentException("The usbmuxd socket path must be absolute.", nameof(socketPath));
        }

        this.endpoint = new UnixDomainSocketEndPoint(socketPath);
    }

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(this.endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new IOException($"Cannot connect to usbmuxd ({this.endpoint}). "
                                  + "Install and start usbmuxd, check socket permissions, and establish trust with the unlocked iPhone.", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
