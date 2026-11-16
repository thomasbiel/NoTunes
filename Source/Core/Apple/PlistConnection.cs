using System.Buffers.Binary;

namespace NoTunes.Apple;

// Lockdown uses big-endian XML frames. ATC uses little-endian binary frames.
public sealed class PlistConnection(Stream stream, TimeSpan timeout, bool atc = false) : IAsyncDisposable
{
    private const int HeaderBytes = sizeof(int);
    private const string ConnectionClosedMessage = "The device connection is closed.";
    private Stream stream = stream;
    private bool broken;

    public Stream Stream => stream;

    public void UpgradeStream(Stream encryptedStream)
    {
        if (broken) throw new IOException(ConnectionClosedMessage);
        stream = encryptedStream;
    }

    public async Task SendAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
    {
        if (broken) throw new IOException(ConnectionClosedMessage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var payload = atc ? BinaryPlist.Write(message) : Plist.ToXml(message);
            var packet = new byte[HeaderBytes + payload.Length];
            if (atc) BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
            else BinaryPrimitives.WriteInt32BigEndian(packet, payload.Length);
            payload.CopyTo(packet, HeaderBytes);
            await stream.WriteAsync(packet, deadline.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync();
            throw new IOException("Timed out sending a device message.", e);
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task<Dictionary<string, object>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (broken) throw new IOException(ConnectionClosedMessage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var header = new byte[HeaderBytes];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var length = atc ? BinaryPrimitives.ReadInt32LittleEndian(header)
                : BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 1 || length > Plist.MaxBytes) throw new InvalidDataException("Invalid device message length.");
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, deadline.Token);
            return Plist.Parse(payload).AsDict();
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync();
            throw new IOException("Timed out waiting for an iPhone response. Unlock the iPhone and reconnect it.", e);
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task<Dictionary<string, object>> ExchangeAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
    {
        await SendAsync(message, cancellationToken);
        return await ReceiveAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (broken) return;
        broken = true;
        await stream.DisposeAsync();
    }
}
