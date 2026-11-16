using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Tests;

internal sealed class PairedTlsServer(TlsTestCredentials credentials) : DefaultTlsServer(new BcTlsCrypto())
{
    public bool AuthenticatedClient { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

    protected override TlsCredentialedSigner GetRsaSignerCredentials()
    {
        var crypto = (BcTlsCrypto)this.Crypto;
        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(this.m_context),
            crypto,
            credentials.DeviceKeys.Private,
            new Certificate([crypto.CreateCertificate(credentials.DeviceCertificate.GetEncoded())]),
            SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.rsa));
    }

    public override CertificateRequest GetCertificateRequest()
    {
        return new([ClientCertificateType.rsa_sign], [SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.rsa)], []);
    }

    public override void NotifyClientCertificate(Certificate certificate)
    {
        this.AuthenticatedClient = !certificate.IsEmpty && certificate.GetCertificateAt(0).GetEncoded().AsSpan().SequenceEqual(credentials.RootCertificate.GetEncoded());
        if (!this.AuthenticatedClient) throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }
}
