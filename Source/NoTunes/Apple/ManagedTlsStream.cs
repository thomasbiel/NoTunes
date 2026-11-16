using System.Security.Authentication;
using System.Security.Cryptography;
using Org.BouncyCastle.Tls;

namespace NoTunes.Apple;

// Managed TLS over an async USB tunnel. No Windows key storage, native TLS DLLs or global TLS changes.
// A connection serializes its operations, matching the request/reply protocols using this stream.
public sealed class ManagedTlsStream : Stream
{
    private const int EncryptedRecordBufferBytes = 18 * 1024;
    private const int MaxPlaintextRecordBytes = 16 * 1024;
    private readonly Stream raw;
    private readonly TlsClientProtocol protocol;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly byte[] incoming = new byte[EncryptedRecordBufferBytes];
    private readonly byte[] outgoing = new byte[EncryptedRecordBufferBytes];
    private int disposed;

    private ManagedTlsStream(Stream raw, TlsClientProtocol protocol)
    {
        this.raw = raw;
        this.protocol = protocol;
    }

    public static async Task<ManagedTlsStream> ConnectAsync(Stream raw, PairingIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var protocol = new TlsClientProtocol();
        var stream = new ManagedTlsStream(raw, protocol);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            protocol.Connect(new PairedTlsClient(identity));
            await stream.FlushRecordsAsync(deadline.Token);
            while (!protocol.IsConnected)
            {
                if (protocol.IsClosed) throw new AuthenticationException("The iPhone closed the TLS handshake.");

                await stream.ReceiveRecordsAsync(deadline.Token);
                await stream.FlushRecordsAsync(deadline.Token);
            }

            return stream;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            throw new IOException("Timed out establishing the encrypted iPhone connection.", e);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public override bool CanRead => this.disposed == 0;

    public override bool CanWrite => this.disposed == 0;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await this.operation.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed != 0, this);
            if (buffer.IsEmpty)
                return 0;
            while (this.protocol.GetAvailableInputBytes() == 0)
            {
                if (this.protocol.IsClosed)
                    return 0;
                await this.ReceiveRecordsAsync(cancellationToken);
                await this.FlushRecordsAsync(cancellationToken);
            }

            var cleartext = new byte[Math.Min(buffer.Length, MaxPlaintextRecordBytes)];
            try
            {
                var count = this.protocol.ReadInput(cleartext, 0, cleartext.Length);
                cleartext.AsMemory(0, count).CopyTo(buffer);
                return count;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cleartext);
            }
        }
        catch
        {
            this.Dispose();
            throw;
        }
        finally
        {
            this.operation.Release();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await this.operation.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed != 0, this);
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length, MaxPlaintextRecordBytes);
                var cleartext = buffer[..count].ToArray();
                try
                {
                    this.protocol.WriteApplicationData(cleartext, 0, cleartext.Length);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(cleartext);
                }

                await this.FlushRecordsAsync(cancellationToken);
                buffer = buffer[count..];
            }
        }
        catch
        {
            this.Dispose();
            throw;
        }
        finally
        {
            this.operation.Release();
        }
    }

    private async Task ReceiveRecordsAsync(CancellationToken cancellationToken)
    {
        var count = await this.raw.ReadAsync(this.incoming, cancellationToken);
        if (count == 0) throw new EndOfStreamException("The iPhone closed the encrypted connection unexpectedly.");

        this.protocol.OfferInput(this.incoming, 0, count);
    }

    private async Task FlushRecordsAsync(CancellationToken cancellationToken)
    {
        while (this.protocol.GetAvailableOutputBytes() > 0)
        {
            var count = this.protocol.ReadOutput(this.outgoing, 0, this.outgoing.Length);
            await this.raw.WriteAsync(this.outgoing.AsMemory(0, count), cancellationToken);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush() => this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await this.operation.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed != 0, this);
            await this.FlushRecordsAsync(cancellationToken);
            await this.raw.FlushAsync(cancellationToken);
        }
        catch
        {
            this.Dispose();
            throw;
        }
        finally
        {
            this.operation.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            this.raw.Dispose();
            try
            {
                this.protocol.Close();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(this.incoming);
                CryptographicOperations.ZeroMemory(this.outgoing);
            }
        }

        base.Dispose(disposing);
    }
}
