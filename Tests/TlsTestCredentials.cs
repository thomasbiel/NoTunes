using System.Text;
using NoTunes.Apple;
using Org.BouncyCastle.Asn1;
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

    public Dictionary<string, object> RecordWithEmptyIssuers()
    {
        var record = this.Record();
        record["RootCertificate"] = WithEmptyIssuer(this.RootCertificate, this.RootKeys.Private);
        record["DeviceCertificate"] = WithEmptyIssuer(this.DeviceCertificate, this.RootKeys.Private);
        return record;
    }

    private static byte[] WithEmptyIssuer(X509Certificate certificate, AsymmetricKeyParameter issuerKey)
    {
        var original = Asn1Sequence.GetInstance(certificate.GetEncoded());
        var tbs = Asn1Sequence.GetInstance(original[0]);
        var fields = new Asn1Encodable[tbs.Count];
        for (var index = 0; index < fields.Length; index++)
        {
            fields[index] = tbs[index];
        }

        var issuerIndex = fields[0] is Asn1TaggedObject ? 3 : 2;
        fields[issuerIndex] = new DerSequence();
        var encodedTbs = new DerSequence(fields).GetEncoded();
        var signer = SignerUtilities.GetSigner("SHA256WITHRSA");
        signer.Init(true, issuerKey);
        signer.BlockUpdate(encodedTbs, 0, encodedTbs.Length);
        return new DerSequence(Asn1Object.FromByteArray(encodedTbs), original[1], new DerBitString(signer.GenerateSignature())).GetEncoded();
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
