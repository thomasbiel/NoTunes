using System.Security.Cryptography;

namespace NoTunes;

internal static class ContentHash
{
    // The single algorithm setting also preserves the existing JSON field name.
    public const string AlgorithmName = "Sha1";

    public static HashAlgorithmName Algorithm { get; } = new(AlgorithmName.ToUpperInvariant());

    public static int DigestBytes { get; } = GetDigestBytes();

    public static bool SupportsAfcFileHash => Algorithm == HashAlgorithmName.SHA1;

    public static string Compute(ReadOnlySpan<byte> bytes)
    {
        return Format(CryptographicOperations.HashData(Algorithm, bytes));
    }

    public static string Compute(Stream stream)
    {
        return Format(CryptographicOperations.HashData(Algorithm, stream));
    }

    public static async Task<string> ComputeAsync(Stream stream, CancellationToken cancellationToken)
    {
        return Format(await CryptographicOperations.HashDataAsync(Algorithm, stream, cancellationToken));
    }

    public static IncrementalHash CreateIncremental()
    {
        return IncrementalHash.CreateHash(Algorithm);
    }

    public static HashAlgorithm CreateAlgorithm()
    {
        return Algorithm.Name switch
        {
            "SHA1" => SHA1.Create(),
            "SHA256" => SHA256.Create(),
            "SHA384" => SHA384.Create(),
            "SHA512" => SHA512.Create(),
            "MD5" => MD5.Create(),
            _ => throw new NotSupportedException($"Unsupported content hash algorithm: {Algorithm.Name}")
        };
    }

    public static string Finish(IncrementalHash hash)
    {
        return Format(hash.GetHashAndReset());
    }

    public static string Format(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != DigestBytes) throw new InvalidDataException("Invalid content hash digest length.");

        return Convert.ToHexString(digest);
    }

    public static bool IsValid(string? value)
    {
        return value is not null && value.Length == DigestBytes * 2 && value.All(char.IsAsciiHexDigit);
    }

    private static int GetDigestBytes()
    {
        using var hash = CreateIncremental();
        return hash.HashLengthInBytes;
    }
}
