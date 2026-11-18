using System.Security.Cryptography;

namespace NoTunes.Apple;

// Fixed-request compatibility profile documented by rustunes; see THIRD-PARTY-NOTICES.md.
internal sealed class GrappaSigner : IDisposable
{
    private const int ReplyLength = 83;
    private const byte EnvelopeVersion = 1;
    private const int ControlOffset = 18;
    private const byte CiphertextLength = 0x40;
    private const int IvOffset = 2;
    private const int CiphertextOffset = 19;
    private const int AesKeyLength = 16;
    private const byte FixedKeyByte = 0x95;
    private const int FirstKeyOffset = 1;
    private const int SecondKeyOffset = 39;
    private const int MaskOffset = 18;
    private const int SignatureLength = 21;
    private const string KeyXorHex = "C6C7C4C5CACBC8C9CECFCCCDB2B3B0B1";
    private const string WrapKeyHex = "7776EB98AB6CE602E8E892E41BA7E915";
    private const string TagXorHex = "F9FAFBFCFDFEFFF0F1F2F3F4F5F6F7E8E9EAEBEC";
    private const string HostRequestHex =
        "0101111111111111111111111111111111110440BC2785E0DBF166361E07980A5EA48DBA95B3B8EA265D62AEFEA51BB7B190E0B77126290AD39BB13FECC08C25A9561C517AC11E64905DA029E61BDFD0BA22C313";

    internal static byte[] HostRequest => Convert.FromHexString(HostRequestHex);

    private readonly byte[] key;
    private readonly byte[] mask;

    public GrappaSigner(byte[] reply)
    {
        if (reply.Length != ReplyLength || reply[0] != EnvelopeVersion || reply[1] != EnvelopeVersion
            || reply[ControlOffset] != CiphertextLength)
        {
            throw new InvalidDataException("Unsupported Grappa reply envelope.");
        }

        using var cipher = Aes.Create();
        cipher.Key = [.. Enumerable.Repeat(FixedKeyByte, AesKeyLength)];
        var decoded = cipher.DecryptCbc(reply.AsSpan(CiphertextOffset), reply.AsSpan(IvOffset, AesKeyLength), PaddingMode.None);
        try
        {
            var seed = Convert.FromHexString(KeyXorHex);
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] ^= (byte)(decoded[i + FirstKeyOffset] ^ decoded[i + SecondKeyOffset]);
            }

            cipher.Key = Convert.FromHexString(WrapKeyHex);
            this.key = cipher.EncryptEcb(seed, PaddingMode.None);
            CryptographicOperations.ZeroMemory(seed);
            this.mask = Convert.FromHexString(TagXorHex);
            for (var i = 0; i < this.mask.Length; i++)
            {
                this.mask[i] ^= decoded[i + MaskOffset];
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public byte[] Sign(byte[] operation)
    {
        if (operation.Length is < 1 or > SyncProtocol.MaxOperationBytes)
        {
            throw new InvalidDataException("Operation file must contain 1 byte to 4 MiB.");
        }

        var tag = HMACSHA1.HashData(this.key, operation);
        var result = new byte[SignatureLength];
        result[0] = EnvelopeVersion;
        for (var i = 0; i < tag.Length; i++)
        {
            result[i + 1] = (byte)(tag[i] ^ this.mask[i]);
        }

        CryptographicOperations.ZeroMemory(tag);
        return result;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(this.key);
        CryptographicOperations.ZeroMemory(this.mask);
    }
}
