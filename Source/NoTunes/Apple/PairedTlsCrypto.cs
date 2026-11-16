using System.Security.Cryptography;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Apple;

internal sealed class PairedTlsCrypto(PairingIdentity identity) : BcTlsCrypto
{
    public override TlsCertificate CreateCertificate(short type, byte[] encoding)
    {
        if (type == CertificateType.X509
            && (CryptographicOperations.FixedTimeEquals(encoding, identity.ClientCertificate)
                || CryptographicOperations.FixedTimeEquals(encoding, identity.DeviceCertificate)))
            return new PairedTlsCertificate(this, encoding);

        return base.CreateCertificate(type, encoding);
    }
}
