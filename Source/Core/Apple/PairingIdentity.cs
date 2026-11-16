using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.X509;

namespace NoTunes.Apple;

public sealed class PairingIdentity
{
    private static class Keys
    {
        public const string RootCertificate = "RootCertificate";
        public const string DeviceCertificate = "DeviceCertificate";
        public const string RootPrivateKey = "RootPrivateKey";
    }

    internal AsymmetricKeyParameter PrivateKey { get; }
    internal byte[] ClientCertificate { get; }
    internal byte[] DeviceCertificate { get; }

    private PairingIdentity(AsymmetricKeyParameter privateKey, byte[] clientCertificate, byte[] deviceCertificate)
    {
        PrivateKey = privateKey;
        ClientCertificate = clientCertificate;
        DeviceCertificate = deviceCertificate;
    }

    public static PairingIdentity FromRecord(Dictionary<string, object> record)
    {
        try
        {
            // Apple's pairing root is also the client identity used by libimobiledevice's TLS transport.
            // Decode directly in managed memory: do not import private keys into Windows key containers.
            var parser = new X509CertificateParser();
            var client = parser.ReadCertificate(Bytes(record, Keys.RootCertificate))
                ?? throw new InvalidDataException("The saved pairing record does not contain a valid RootCertificate.");
            var device = parser.ReadCertificate(Bytes(record, Keys.DeviceCertificate))
                ?? throw new InvalidDataException("The saved pairing record does not contain a valid DeviceCertificate.");
            using var reader = new StringReader(Encoding.UTF8.GetString(Bytes(record, Keys.RootPrivateKey)));
            var pem = new PemReader(reader).ReadObject();
            var key = pem is AsymmetricCipherKeyPair pair ? pair.Private : pem as AsymmetricKeyParameter;
            if (key is not RsaPrivateCrtKeyParameters rsa || client.GetPublicKey() is not RsaKeyParameters publicKey
                || !rsa.Modulus.Equals(publicKey.Modulus) || !rsa.PublicExponent.Equals(publicKey.Exponent))
                throw new InvalidDataException("The pairing certificate and RSA private key do not match.");
            return new PairingIdentity(key, client.GetEncoded(), device.GetEncoded());
        }
        catch (Exception e) when (e is Org.BouncyCastle.Security.GeneralSecurityException
            or PemException or ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("The saved iPhone pairing credentials are malformed.", e);
        }
    }

    private static byte[] Bytes(Dictionary<string, object> record, string key) =>
        record.TryGetValue(key, out var value) && value is byte[] bytes ? bytes
            : throw new InvalidDataException($"The saved pairing record is missing {key}.");
}
