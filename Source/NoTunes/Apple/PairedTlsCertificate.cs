using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Apple;

internal sealed class PairedTlsCertificate : BcTlsCertificate
{
    private readonly byte[] encoding;

    public PairedTlsCertificate(BcTlsCrypto crypto, byte[] encoding)
        : base(crypto, PairingCertificate.ParseStructure(encoding))
    {
        this.encoding = encoding.ToArray();
    }

    public override byte[] GetEncoded() => this.encoding.ToArray();
}
