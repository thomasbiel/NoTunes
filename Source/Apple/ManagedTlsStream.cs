using System.Security.Authentication;
using System.Security.Cryptography;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Apple;

// Managed TLS over an async USB tunnel. No Windows key storage, native TLS DLLs or global TLS changes.
// A connection serializes its operations, matching the request/reply protocols using this stream.
public sealed class ManagedTlsStream : Stream
{
    private readonly Stream raw;
    private readonly TlsClientProtocol protocol;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly byte[] incoming = new byte[18 * 1024];
    private readonly byte[] outgoing = new byte[18 * 1024];
    private int disposed;

    private ManagedTlsStream(Stream raw, TlsClientProtocol protocol)
    {
        this.raw = raw;
        this.protocol = protocol;
    }

    public static async Task<ManagedTlsStream> ConnectAsync(Stream raw, PairingIdentity identity,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var protocol = new TlsClientProtocol();
        var stream = new ManagedTlsStream(raw, protocol);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            protocol.Connect(new PairedClient(identity));
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

    public override bool CanRead => disposed == 0;
    public override bool CanWrite => disposed == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (buffer.IsEmpty) return 0;
            while (protocol.GetAvailableInputBytes() == 0)
            {
                if (protocol.IsClosed) return 0;
                await ReceiveRecordsAsync(cancellationToken);
                await FlushRecordsAsync(cancellationToken);
            }
            var cleartext = new byte[Math.Min(buffer.Length, 16 * 1024)];
            try
            {
                var count = protocol.ReadInput(cleartext, 0, cleartext.Length);
                cleartext.AsMemory(0, count).CopyTo(buffer);
                return count;
            }
            finally { CryptographicOperations.ZeroMemory(cleartext); }
        }
        catch { Dispose(); throw; }
        finally { operation.Release(); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length, 16 * 1024);
                var cleartext = buffer[..count].ToArray();
                try { protocol.WriteApplicationData(cleartext, 0, cleartext.Length); }
                finally { CryptographicOperations.ZeroMemory(cleartext); }
                await FlushRecordsAsync(cancellationToken);
                buffer = buffer[count..];
            }
        }
        catch { Dispose(); throw; }
        finally { operation.Release(); }
    }

    private async Task ReceiveRecordsAsync(CancellationToken cancellationToken)
    {
        var count = await raw.ReadAsync(incoming, cancellationToken);
        if (count == 0) throw new EndOfStreamException("The iPhone closed the encrypted connection unexpectedly.");
        protocol.OfferInput(incoming, 0, count);
    }

    private async Task FlushRecordsAsync(CancellationToken cancellationToken)
    {
        while (protocol.GetAvailableOutputBytes() > 0)
        {
            var count = protocol.ReadOutput(outgoing, 0, outgoing.Length);
            await raw.WriteAsync(outgoing.AsMemory(0, count), cancellationToken);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await operation.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await FlushRecordsAsync(cancellationToken);
            await raw.FlushAsync(cancellationToken);
        }
        catch { Dispose(); throw; }
        finally { operation.Release(); }
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            raw.Dispose();
            try { protocol.Close(); }
            finally
            {
                CryptographicOperations.ZeroMemory(incoming);
                CryptographicOperations.ZeroMemory(outgoing);
            }
        }
        base.Dispose(disposing);
    }

    private sealed class PairedClient(PairingIdentity identity) : DefaultTlsClient(new BcTlsCrypto())
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];
        public override TlsAuthentication GetAuthentication() => new Authentication(m_context, identity);

        private sealed class Authentication(TlsContext context, PairingIdentity identity) : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
                var chain = serverCertificate.Certificate;
                if (chain.IsEmpty || !CryptographicOperations.FixedTimeEquals(
                    chain.GetCertificateAt(0).GetEncoded(), identity.DeviceCertificate))
                    throw new AuthenticationException("The iPhone TLS certificate does not match the saved pairing record.");
            }

            public TlsCredentials GetClientCredentials(CertificateRequest request)
            {
                if (request.CertificateTypes is null || !request.CertificateTypes.Contains(ClientCertificateType.rsa_sign))
                    throw new AuthenticationException("The iPhone did not request a supported RSA pairing identity.");
                var crypto = (BcTlsCrypto)context.Crypto;
                var certificate = new Certificate([crypto.CreateCertificate(identity.ClientCertificate)]);
                var algorithm = TlsUtilities.ChooseSignatureAndHashAlgorithm(context,
                    request.SupportedSignatureAlgorithms, SignatureAlgorithm.rsa);
                return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto,
                    identity.PrivateKey, certificate, algorithm);
            }
        }
    }
}
