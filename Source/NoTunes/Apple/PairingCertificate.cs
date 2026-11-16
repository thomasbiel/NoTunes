using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Utilities.IO.Pem;

namespace NoTunes.Apple;

internal static class PairingCertificate
{
    public static byte[] Decode(byte[] content)
    {
        if (content.Length == 0) throw new InvalidDataException("The saved pairing certificate is empty.");

        if (content[0] == 0x30) return content.ToArray();
        using var reader = new StringReader(Encoding.UTF8.GetString(content));
        var pem = new PemReader(reader).ReadPemObject();
        if (pem is null || !StringComparer.Ordinal.Equals(pem.Type, "CERTIFICATE"))
            throw new InvalidDataException("The saved pairing certificate is not a PEM certificate.");

        return pem.Content;
    }

    public static X509CertificateStructure ParseStructure(byte[] encoded)
    {
        try
        {
            var certificate = Asn1Sequence.GetInstance(Asn1Object.FromByteArray(encoded));
            if (certificate.Count != 3) throw new InvalidDataException("Invalid pairing certificate structure.");

            var tbs = Asn1Sequence.GetInstance(certificate[0]);
            if (tbs.Count is < 6 or > 10) throw new InvalidDataException("Invalid pairing certificate body.");

            var issuerIndex = tbs[0] is Asn1TaggedObject ? 3 : 2;
            if (X509Name.GetInstance(tbs[issuerIndex]).IsEmpty)
            {
                // Apple pairing certificates can have an empty issuer. Only the internal crypto view
                // gets a placeholder; TLS transmission and certificate pinning use the original DER.
                var fields = new Asn1Encodable[tbs.Count];
                for (var index = 0; index < fields.Length; index++) fields[index] = tbs[index];
                fields[issuerIndex] = new X509Name("CN=Apple Pairing");
                certificate = new DerSequence(new DerSequence(fields), certificate[1], certificate[2]);
            }

            return X509CertificateStructure.GetInstance(certificate);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            throw new InvalidDataException("The saved pairing certificate is malformed.", e);
        }
    }
}
