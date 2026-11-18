using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Utilities.IO.Pem;

namespace NoTunes.Apple;

internal static class PairingCertificate
{
    private const byte DerSequenceTag = 0x30;
    private const string PemCertificateType = "CERTIFICATE";
    private const string EmptyIssuerPlaceholder = "CN=Apple Pairing";
    private const int CertificateFieldCount = 3;
    private const int BodyIndex = 0;
    private const int SignatureAlgorithmIndex = 1;
    private const int SignatureValueIndex = 2;
    private const int MinimumBodyFieldCount = 6;
    private const int MaximumBodyFieldCount = 10;
    private const int IssuerIndexWithoutVersion = 2;
    private const int IssuerIndexWithVersion = 3;

    public static byte[] Decode(byte[] content)
    {
        if (content.Length == 0) throw new InvalidDataException("The saved pairing certificate is empty.");

        if (content[0] == DerSequenceTag) return [.. content];

        using var reader = new StringReader(Encoding.UTF8.GetString(content));
        var pem = new PemReader(reader).ReadPemObject();
        if (pem is null || !StringComparer.Ordinal.Equals(pem.Type, PemCertificateType))
        {
            throw new InvalidDataException("The saved pairing certificate is not a PEM certificate.");
        }

        return pem.Content;
    }

    public static X509CertificateStructure ParseStructure(byte[] encoded)
    {
        try
        {
            var certificate = Asn1Sequence.GetInstance(Asn1Object.FromByteArray(encoded));
            if (certificate.Count != CertificateFieldCount)
            {
                throw new InvalidDataException("Invalid pairing certificate structure.");
            }

            var tbs = Asn1Sequence.GetInstance(certificate[BodyIndex]);
            if (tbs.Count is < MinimumBodyFieldCount or > MaximumBodyFieldCount)
            {
                throw new InvalidDataException("Invalid pairing certificate body.");
            }

            var issuerIndex = tbs[BodyIndex] is Asn1TaggedObject ? IssuerIndexWithVersion : IssuerIndexWithoutVersion;
            if (X509Name.GetInstance(tbs[issuerIndex]).IsEmpty)
            {
                // Apple pairing certificates can have an empty issuer. Only the internal crypto view
                // gets a placeholder; TLS transmission and certificate pinning use the original DER.
                var fields = new Asn1Encodable[tbs.Count];
                for (var index = 0; index < fields.Length; index++)
                {
                    fields[index] = tbs[index];
                }

                fields[issuerIndex] = new X509Name(EmptyIssuerPlaceholder);
                certificate = new DerSequence(new DerSequence(fields),
                    certificate[SignatureAlgorithmIndex], certificate[SignatureValueIndex]);
            }

            return X509CertificateStructure.GetInstance(certificate);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            throw new InvalidDataException("The saved pairing certificate is malformed.", e);
        }
    }
}
