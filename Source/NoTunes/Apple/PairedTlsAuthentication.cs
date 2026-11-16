using System.Security.Authentication;
using System.Security.Cryptography;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Apple;

internal sealed class PairedTlsAuthentication(TlsContext context, PairingIdentity identity) : TlsAuthentication
{
    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        var chain = serverCertificate.Certificate;
        if (chain.IsEmpty || !CryptographicOperations.FixedTimeEquals(chain.GetCertificateAt(0).GetEncoded(), identity.DeviceCertificate))
            throw new AuthenticationException("The iPhone TLS certificate does not match the saved pairing record.");
    }

    public TlsCredentials GetClientCredentials(CertificateRequest request)
    {
        if (request.CertificateTypes is null || !request.CertificateTypes.Contains(ClientCertificateType.rsa_sign))
            throw new AuthenticationException("The iPhone did not request a supported RSA pairing identity.");

        var crypto = (BcTlsCrypto)context.Crypto;
        var certificate = new Certificate([crypto.CreateCertificate(identity.ClientCertificate)]);
        var algorithm = TlsUtilities.ChooseSignatureAndHashAlgorithm(context, request.SupportedSignatureAlgorithms, SignatureAlgorithm.rsa);
        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(context),
            crypto,
            identity.PrivateKey,
            certificate,
            algorithm);
    }
}
