using System.Text;
using NoTunes.Apple;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using X509Name = Org.BouncyCastle.Asn1.X509.X509Name;

namespace NoTunes.Tests;

internal sealed class TlsTestCredentials
{
    public AsymmetricCipherKeyPair RootKeys { get; } = GenerateKey();

    public AsymmetricCipherKeyPair DeviceKeys { get; } = GenerateKey();

    public X509Certificate RootCertificate { get; }

    public X509Certificate DeviceCertificate { get; }

    public PairingIdentity Identity => PairingIdentity.FromRecord(this.Record());

    public TlsTestCredentials()
    {
        this.RootCertificate = CreateCertificate("Root", this.RootKeys.Public, this.RootKeys.Private);
        this.DeviceCertificate = CreateCertificate("Device", this.DeviceKeys.Public, this.RootKeys.Private);
    }

    public Dictionary<string, object> Record()
    {
        return Plist.Dict(("RootCertificate", Encoding.UTF8.GetBytes(Pem(this.RootCertificate))), ("RootPrivateKey", Encoding.UTF8.GetBytes(Pem(this.RootKeys.Private))), ("DeviceCertificate", Encoding.UTF8.GetBytes(Pem(this.DeviceCertificate))));
    }

    private static AsymmetricCipherKeyPair GenerateKey()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        return generator.GenerateKeyPair();
    }

    private static X509Certificate CreateCertificate(string name, AsymmetricKeyParameter publicKey, AsymmetricKeyParameter issuerKey)
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

    internal static string Pem(object value)
    {
        using var writer = new StringWriter();
        new PemWriter(writer).WriteObject(value);
        return writer.ToString();
    }
}
