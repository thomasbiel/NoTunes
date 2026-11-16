using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NoTunes.Apple;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Tls;

namespace NoTunes.Tests;

public sealed class ManagedTlsTests
{
    private static readonly Lazy<TlsTestCredentials> Keys = new(() => new TlsTestCredentials());

    [Test]
    public async Task Authenticates_Both_Peers_And_Transfers_Multiple_Tls_Records()
    {
        var credentials = Keys.Value;
        var payload = Enumerable.Range(0, 80_000).Select(i => (byte)i).ToArray();
        var server = new PairedTlsServer(credentials);
        await WithPeer(server, payload, async raw =>
        {
            await using var tls = await ManagedTlsStream.ConnectAsync(
                raw,
                credentials.Identity,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await tls.WriteAsync(payload);
            var echoed = new byte[payload.Length];
            await tls.ReadExactlyAsync(echoed);
            Assert.That(echoed, Is.EqualTo(payload));
        });
        Assert.That(server.AuthenticatedClient, Is.True);
    }

    [Test]
    public async Task Rejects_A_Device_Certificate_That_Does_Not_Match_The_Pairing_Record()
    {
        var credentials = Keys.Value;
        var wrongRecord = credentials.Record();
        wrongRecord["DeviceCertificate"] = Encoding.UTF8.GetBytes(TlsTestCredentials.Pem(credentials.RootCertificate));
        var identity = PairingIdentity.FromRecord(wrongRecord);
        await WithPeer(
            new PairedTlsServer(credentials),
            [],
            async raw =>
            {
                await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(
                    raw,
                    identity,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None), Throws.InstanceOf<IOException>().Or.InstanceOf<AuthenticationException>());
            },
            expectHandshakeFailure: true);
    }

    [Test]
    public async Task Handshake_Timeout_Closes_The_Underlying_Connection()
    {
        var raw = new SilentPeerStream();
        await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(
            raw,
            Keys.Value.Identity,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("Timed out"));
        Assert.That(raw.WasDisposed, Is.True);
    }

    [Test]
    public async Task Caller_Cancellation_Is_Not_Reported_As_A_Timeout()
    {
        var raw = new SilentPeerStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(
            raw,
            Keys.Value.Identity,
            TimeSpan.FromSeconds(5),
            cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(raw.WasDisposed, Is.True);
    }

    [Test]
    public void Rejects_Missing_Or_Mismatched_Pairing_Credentials()
    {
        Assert.Throws<InvalidDataException>(() => PairingIdentity.FromRecord([]));
        var record = Keys.Value.Record();
        record["RootPrivateKey"] = Encoding.UTF8.GetBytes(TlsTestCredentials.Pem(Keys.Value.DeviceKeys.Private));
        Assert.Throws<InvalidDataException>(() => PairingIdentity.FromRecord(record));
    }

    [TestCase("RootCertificate", "")]
    [TestCase("DeviceCertificate", "")]
    [TestCase("RootCertificate", "not a certificate")]
    [TestCase("DeviceCertificate", "not a certificate")]
    public void Rejects_Empty_Or_Unparseable_Pairing_Certificates(string field, string content)
    {
        var record = Keys.Value.Record();
        record[field] = Encoding.UTF8.GetBytes(content);
        Assert.That(() => PairingIdentity.FromRecord(record), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("RootCertificate")]
    [TestCase("DeviceCertificate")]
    public void Rejects_Excessively_Nested_Pairing_Certificates(string field)
    {
        var credentials = Keys.Value;
        var original = field == "RootCertificate" ? credentials.RootCertificate : credentials.DeviceCertificate;
        var certificate = Asn1Sequence.GetInstance(original.GetEncoded());
        Asn1Encodable parameters = DerNull.Instance;
        // Exceed the parser's depth limit without risking a stack overflow in older versions.
        for (var i = 0; i < 128; i++)
        {
            parameters = new DerSequence(parameters);
        }

        var signatureAlgorithm = new AlgorithmIdentifier(AlgorithmIdentifier.GetInstance(certificate[1]).Algorithm, parameters);
        var nestedCertificate = new DerSequence(certificate[0], signatureAlgorithm, certificate[2]);
        var record = credentials.Record();
        record[field] = nestedCertificate.GetEncoded();
        Assert.That(() => PairingIdentity.FromRecord(record), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("RootCertificate")]
    [TestCase("DeviceCertificate")]
    public void Accepts_Empty_Issuer_Pairing_Certificates_Without_Changing_Their_Bytes(string field)
    {
        var record = Keys.Value.Record();
        var encoded = (byte[])Keys.Value.RecordWithEmptyIssuers()[field];
        record[field] = encoded;
        var identity = PairingIdentity.FromRecord(record);
        var actual = StringComparer.Ordinal.Equals(field, "RootCertificate")
            ? identity.ClientCertificate : identity.DeviceCertificate;
        Assert.That(actual, Is.EqualTo(encoded));
    }

    [Test]
    public void Preserves_The_Original_Encoding_Of_Known_Empty_Issuer_Tls_Certificates()
    {
        var record = Keys.Value.RecordWithEmptyIssuers();
        var identity = PairingIdentity.FromRecord(record);
        var crypto = new PairedTlsCrypto(identity);
        var certificate = crypto.CreateCertificate(identity.DeviceCertificate);
        Assert.That(certificate.GetEncoded(), Is.EqualTo((byte[])record["DeviceCertificate"]));
    }

    [Test]
    public void Rejects_Unknown_Empty_Issuer_Tls_Certificates()
    {
        var crypto = new PairedTlsCrypto(Keys.Value.Identity);
        var encoded = (byte[])Keys.Value.RecordWithEmptyIssuers()["DeviceCertificate"];
        Assert.That(() => crypto.CreateCertificate(encoded), Throws.InstanceOf<IOException>());
    }

    [Test]
    public async Task Authenticates_Empty_Issuer_Pairing_Certificates_Over_Tls()
    {
        var credentials = Keys.Value;
        var record = credentials.RecordWithEmptyIssuers();
        var identity = PairingIdentity.FromRecord(record);
        var server = new PairedTlsServer(credentials, record);
        byte[] payload = [1, 2, 3];
        await WithPeer(server, payload, async raw =>
        {
            await using var tls = await ManagedTlsStream.ConnectAsync(
                raw,
                identity,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await tls.WriteAsync(payload);
            var echoed = new byte[payload.Length];
            await tls.ReadExactlyAsync(echoed);
            Assert.That(echoed, Is.EqualTo(payload));
        });
        Assert.That(server.AuthenticatedClient, Is.True);
    }

    [Test]
    public async Task Rejects_A_Different_Empty_Issuer_Certificate_With_The_Same_Public_Key()
    {
        var credentials = Keys.Value;
        var record = credentials.RecordWithEmptyIssuers();
        var wrongRecord = new Dictionary<string, object>(record);
        var changed = ((byte[])record["DeviceCertificate"]).ToArray();
        changed[^1] ^= 1;
        wrongRecord["DeviceCertificate"] = changed;
        var identity = PairingIdentity.FromRecord(wrongRecord);
        await WithPeer(
            new PairedTlsServer(credentials, record),
            [],
            async raw =>
            {
                await Assert.ThatAsync(async () => await ManagedTlsStream.ConnectAsync(
                    raw,
                    identity,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None), Throws.InstanceOf<IOException>().Or.InstanceOf<AuthenticationException>());
            },
            expectHandshakeFailure: true);
    }

    [TestCase(null)]
    [TestCase(false)]
    [TestCase("true")]
    [TestCase(1L)]
    public async Task Rejects_Unauthenticated_Lockdown_Sessions(object? sslFlag)
    {
        var reply = Plist.Dict(("Request", "StartSession"), ("SessionID", "test-session"));
        if (sslFlag is not null)
            reply["EnableSessionSSL"] = sslFlag;
        await AssertRejectedLockdownSessionAsync(reply, Throws.TypeOf<InvalidDataException>().With.Message.Contains("EnableSessionSSL"));
    }

    [Test]
    public Task Failed_Lockdown_Handshake_Does_Not_Authorize_Service_Access()
    {
        return AssertRejectedLockdownSessionAsync(Plist.Dict(("Request", "StartSession"), ("SessionID", "test-session"), ("EnableSessionSSL", true)), Throws.InstanceOf<IOException>());
    }

    private static async Task AssertRejectedLockdownSessionAsync(Dictionary<string, object> reply, IResolveConstraint failure)
    {
        var (transport, raw) = CreateLockdownPeer(reply);
        var mux = new UsbMuxClient(transport, TimeSpan.FromSeconds(2));
        await using var lockdown = await LockdownClient.ConnectAsync(
            mux,
            new UsbDevice(1, "test", 1),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        await Assert.ThatAsync(async () => await lockdown.StartPairedSessionAsync(CancellationToken.None), failure);
        Assert.That(raw.WasDisposed, Is.True);
        await Assert.ThatAsync(async () => await lockdown.OpenServiceAsync("com.apple.afc", CancellationToken.None), Throws.InstanceOf<IOException>().With.Message.Contains("required"));
        Assert.That(transport.Connections, Is.EqualTo(2));
    }

    private static (ReplyUsbMuxTransport Transport, ScriptedStream Raw) CreateLockdownPeer(Dictionary<string, object> sessionReply)
    {
        var record = Keys.Value.Record();
        record["HostID"] = "test-host";
        record["SystemBUID"] = "test-buid";
        var raw = new ScriptedStream(
        [
            .. ProtocolFrames.UsbMux(1, Plist.Dict(("Number", 0L))),
            .. ProtocolFrames.PlistMessage(Plist.Dict(("Request", "QueryType"), ("Type", "com.apple.mobile.lockdown"))),
            .. ProtocolFrames.PlistMessage(sessionReply)
        ], maxReadBytes: int.MaxValue);
        var pairing = new ScriptedStream(ProtocolFrames.UsbMux(2, Plist.Dict(("PairRecordData", Plist.ToXml(record)))), maxReadBytes: int.MaxValue);
        return (new ReplyUsbMuxTransport(raw, pairing), raw);
    }

    private static async Task WithPeer(PairedTlsServer peer, byte[] payload, Func<Stream, Task> client, bool expectHandshakeFailure = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clientSocket = new TcpClient();
        var server = Task.Run(async () =>
        {
            using var serverSocket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var onCancel = deadline.Token.Register(serverSocket.Dispose);
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
            catch (IOException) when (expectHandshakeFailure)
            {
            }
            finally
            {
                protocol.Close();
            }
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
            try
            {
                await server.WaitAsync(deadline.Token);
            }
            finally
            {
                await deadline.CancelAsync();
            }
        }
    }
}
