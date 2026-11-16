using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using X509Name = Org.BouncyCastle.Asn1.X509.X509Name;

namespace NoTunes.Tests;

public sealed class ManagedTlsTests
{
    private static readonly Lazy<Credentials> Keys = new(() => new Credentials());

    [Test]
    public async Task AuthenticatesBothPeersAndTransfersMultipleTlsRecords()
    {
        var credentials = Keys.Value;
        var payload = Enumerable.Range(0, 80_000).Select(i => (byte)i).ToArray();
        var server = new PairedServer(credentials);
        await WithPeer(server, payload, async raw =>
        {
            await using var tls = await ManagedTlsStream.ConnectAsync(raw, credentials.Identity,
                TimeSpan.FromSeconds(5), CancellationToken.None);
            await tls.WriteAsync(payload);
            var echoed = new byte[payload.Length];
            await tls.ReadExactlyAsync(echoed);
            Assert.That(echoed, Is.EqualTo(payload));
        });
        Assert.That(server.AuthenticatedClient, Is.True);
    }

    [Test]
    public async Task RejectsADeviceCertificateThatDoesNotMatchThePairingRecord()
    {
        var credentials = Keys.Value;
        var wrongRecord = credentials.Record();
        wrongRecord["DeviceCertificate"] = Encoding.UTF8.GetBytes(Pem(credentials.RootCertificate));
        var identity = PairingIdentity.FromRecord(wrongRecord);
        await WithPeer(new PairedServer(credentials), [], async raw =>
        {
            await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(raw, identity,
                    TimeSpan.FromSeconds(5), CancellationToken.None),
                Throws.InstanceOf<IOException>().Or.InstanceOf<AuthenticationException>());
        }, expectHandshakeFailure: true);
    }

    [Test]
    public async Task HandshakeTimeoutClosesTheUnderlyingConnection()
    {
        var raw = new SilentPeerStream();
        await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(raw, Keys.Value.Identity,
            TimeSpan.FromMilliseconds(100), CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("Timed out"));
        Assert.That(raw.WasDisposed, Is.True);
    }

    [Test]
    public async Task CallerCancellationIsNotReportedAsATimeout()
    {
        var raw = new SilentPeerStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(raw, Keys.Value.Identity,
            TimeSpan.FromSeconds(5), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(raw.WasDisposed, Is.True);
    }

    [Test]
    public void RejectsMissingOrMismatchedPairingCredentials()
    {
        Assert.Throws<InvalidDataException>(() => PairingIdentity.FromRecord([]));
        var record = Keys.Value.Record();
        record["RootPrivateKey"] = Encoding.UTF8.GetBytes(Pem(Keys.Value.DeviceKeys.Private));
        Assert.Throws<InvalidDataException>(() => PairingIdentity.FromRecord(record));
    }

    [TestCase("RootCertificate", "")]
    [TestCase("DeviceCertificate", "")]
    [TestCase("RootCertificate", "not a certificate")]
    [TestCase("DeviceCertificate", "not a certificate")]
    public void RejectsEmptyOrUnparseablePairingCertificates(string field, string content)
    {
        var record = Keys.Value.Record();
        record[field] = Encoding.UTF8.GetBytes(content);
        Assert.That(() => PairingIdentity.FromRecord(record), Throws.TypeOf<InvalidDataException>());
    }

    private static async Task WithPeer(PairedServer peer, byte[] payload, Func<Stream, Task> client,
        bool expectHandshakeFailure = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clientSocket = new TcpClient();
        var server = Task.Run(async () =>
        {
            using var serverSocket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var onCancel = deadline.Token.Register(serverSocket.Dispose);
            var protocol = new TlsServerProtocol(serverSocket.GetStream());
            try
            {
                protocol.Accept(peer);
                var received = new byte[payload.Length];
                protocol.Stream.ReadExactly(received);
                Assert.That(received, Is.EqualTo(payload));
                protocol.Stream.Write(received);
                protocol.Stream.Flush();
            }
            catch (IOException) when (expectHandshakeFailure) { }
            finally { protocol.Close(); }
        });
        try
        {
            await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
            await client(clientSocket.GetStream()).WaitAsync(deadline.Token);
        }
        finally
        {
            clientSocket.Dispose();
            listener.Stop();
            try { await server.WaitAsync(deadline.Token); }
            finally { await deadline.CancelAsync(); }
        }
    }

    private sealed class PairedServer(Credentials credentials) : DefaultTlsServer(new BcTlsCrypto())
    {
        public bool AuthenticatedClient { get; private set; }
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];
        protected override TlsCredentialedSigner GetRsaSignerCredentials()
        {
            var crypto = (BcTlsCrypto)Crypto;
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), crypto,
                credentials.DeviceKeys.Private,
                new Certificate([crypto.CreateCertificate(credentials.DeviceCertificate.GetEncoded())]),
                SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.rsa));
        }

        public override CertificateRequest GetCertificateRequest() => new(
            [ClientCertificateType.rsa_sign],
            [SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.rsa)], []);

        public override void NotifyClientCertificate(Certificate certificate)
        {
            AuthenticatedClient = !certificate.IsEmpty && certificate.GetCertificateAt(0).GetEncoded()
                .AsSpan().SequenceEqual(credentials.RootCertificate.GetEncoded());
            if (!AuthenticatedClient) throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }
    }

    private sealed class Credentials
    {
        public AsymmetricCipherKeyPair RootKeys { get; } = GenerateKey();
        public AsymmetricCipherKeyPair DeviceKeys { get; } = GenerateKey();
        public X509Certificate RootCertificate { get; }
        public X509Certificate DeviceCertificate { get; }
        public PairingIdentity Identity => PairingIdentity.FromRecord(Record());

        public Credentials()
        {
            RootCertificate = CreateCertificate("Root", RootKeys.Public, RootKeys.Private);
            DeviceCertificate = CreateCertificate("Device", DeviceKeys.Public, RootKeys.Private);
        }

        public Dictionary<string, object> Record() => Plist.Dict(
            ("RootCertificate", Encoding.UTF8.GetBytes(Pem(RootCertificate))),
            ("RootPrivateKey", Encoding.UTF8.GetBytes(Pem(RootKeys.Private))),
            ("DeviceCertificate", Encoding.UTF8.GetBytes(Pem(DeviceCertificate))));

        private static AsymmetricCipherKeyPair GenerateKey()
        {
            var generator = new RsaKeyPairGenerator();
            generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            return generator.GenerateKeyPair();
        }

        private static X509Certificate CreateCertificate(string name, AsymmetricKeyParameter publicKey,
            AsymmetricKeyParameter issuerKey)
        {
            var generator = new X509V3CertificateGenerator();
            generator.SetSerialNumber(name == "Root" ? BigInteger.One : BigInteger.Two);
            generator.SetIssuerDN(new X509Name("CN=Root"));
            generator.SetSubjectDN(new X509Name("CN=" + name));
            generator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            generator.SetNotAfter(DateTime.UtcNow.AddDays(1));
            generator.SetPublicKey(publicKey);
            return generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", issuerKey));
        }
    }

    private static string Pem(object value)
    {
        using var writer = new StringWriter();
        new PemWriter(writer).WriteObject(value);
        return writer.ToString();
    }

    private sealed class SilentPeerStream : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanWrite => !WasDisposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
