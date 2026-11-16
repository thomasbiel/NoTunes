namespace NoTunes.Platform;

public interface IUsbMuxTransport
{
    // The caller owns the returned stream and supplies the connection deadline.
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
}
