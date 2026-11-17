using NoTunes.Apple;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Tests;

internal sealed class PairedTlsServer : DefaultTlsServer
{
    private readonly TlsTestCredentials credentials;
    private readonly PairingIdentity identity;

    public PairedTlsServer(TlsTestCredentials credentials, Dictionary<string, object>? pairingRecord = null)
        : this(credentials, PairingIdentity.FromRecord(pairingRecord ?? credentials.Record()))
    {
    }

    private PairedTlsServer(TlsTestCredentials credentials, PairingIdentity identity)
        : base(new PairedTlsCrypto(identity))
    {
        this.credentials = credentials;
        this.identity = identity;
    }

    public bool AuthenticatedClient { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

    protected override TlsCredentialedSigner GetRsaSignerCredentials()
    {
        var crypto = (BcTlsCrypto)this.Crypto;
        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(this.m_context),
            crypto,
            this.credentials.DeviceKeys.Private,
            new Certificate([crypto.CreateCertificate(this.identity.DeviceCertificate)]),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.rsa));
    }

    public override CertificateRequest GetCertificateRequest()
    {
        return new([ClientCertificateType.rsa_sign], [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.rsa)], []);
    }

    public override void NotifyClientCertificate(Certificate certificate)
    {
        this.AuthenticatedClient = !certificate.IsEmpty && certificate.GetCertificateAt(0).GetEncoded().AsSpan().SequenceEqual(this.identity.ClientCertificate);
        if (!this.AuthenticatedClient) throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }
}
