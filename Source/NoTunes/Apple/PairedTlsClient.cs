using Org.BouncyCastle.Tls;

namespace NoTunes.Apple;

internal sealed class PairedTlsClient(PairingIdentity identity) : DefaultTlsClient(new PairedTlsCrypto(identity))
{
    protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

    public override TlsAuthentication GetAuthentication() => new PairedTlsAuthentication(this.m_context, identity);
}
