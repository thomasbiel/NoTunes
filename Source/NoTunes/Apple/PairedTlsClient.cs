using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace NoTunes.Apple;

internal sealed class PairedTlsClient(PairingIdentity identity) : DefaultTlsClient(new BcTlsCrypto())
{
    protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

    public override TlsAuthentication GetAuthentication() => new PairedTlsAuthentication(this.m_context, identity);
}
