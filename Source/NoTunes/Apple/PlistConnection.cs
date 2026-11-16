using System.Buffers.Binary;

namespace NoTunes.Apple;

// Lockdown uses big-endian XML frames. ATC uses little-endian binary frames.
public sealed class PlistConnection(Stream stream, TimeSpan timeout, bool atc = false) : IAsyncDisposable
{
    private const int HeaderBytes = sizeof(int);
    private const string ConnectionClosedMessage = "The device connection is closed.";
    private bool broken;

    public Stream Stream { get; private set; } = stream;

    public void UpgradeStream(Stream encryptedStream)
    {
        if (this.broken) throw new IOException(ConnectionClosedMessage);

        this.Stream = encryptedStream;
    }

    public async Task SendAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
    {
        if (this.broken) throw new IOException(ConnectionClosedMessage);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var payload = atc ? BinaryPlist.Write(message) : Plist.ToXml(message);
            var packet = new byte[HeaderBytes + payload.Length];
            if (atc)
                BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
            else
                BinaryPrimitives.WriteInt32BigEndian(packet, payload.Length);
            
            payload.CopyTo(packet, HeaderBytes);
            await this.Stream.WriteAsync(packet, deadline.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await this.DisposeAsync();
            throw new IOException("Timed out sending a device message.", e);
        }
        catch
        {
            await this.DisposeAsync();
            throw;
        }
    }

    public async Task<Dictionary<string, object>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (this.broken) throw new IOException(ConnectionClosedMessage);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var header = new byte[HeaderBytes];
            await this.Stream.ReadExactlyAsync(header, deadline.Token);
            var length = atc ? BinaryPrimitives.ReadInt32LittleEndian(header) : BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is < 1 or > Plist.MaxBytes) throw new InvalidDataException("Invalid device message length.");

            var payload = new byte[length];
            await this.Stream.ReadExactlyAsync(payload, deadline.Token);
            return Plist.Parse(payload).AsDict();
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await this.DisposeAsync();
            throw new IOException("Timed out waiting for an iPhone response. Unlock the iPhone and reconnect it.", e);
        }
        catch
        {
            await this.DisposeAsync();
            throw;
        }
    }

    public async Task<Dictionary<string, object>> ExchangeAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
    {
        await this.SendAsync(message, cancellationToken);
        return await this.ReceiveAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.broken) return;
        this.broken = true;
        await this.Stream.DisposeAsync();
    }
}
