using System.Security.Cryptography;
using System.Text.Json;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ContentHashTests
{
    private static string ExpectedAbc => ContentHash.Algorithm.Name switch
    {
        "SHA1" => "A9993E364706816ABA3E25717850C26C9CD0D89D",
        "SHA256" => "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
        "SHA384" => "CB00753F45A35E8BB5A03D699AC65007272C32AB0EDED1631A8B605A43FF5BED8086072BA1E7CC2358BAECA134C825A7",
        "SHA512" => "DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A"
            + "2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F",
        "MD5" => "900150983CD24FB0D6963F7D28E17F72",
        _ => throw new NotSupportedException("Add a known test vector for the selected content hash.")
    };

    [Test]
    public async Task Uses_The_Same_Algorithm_For_Bytes_Streams_And_Incremental_Hashes()
    {
        var bytes = "abc"u8.ToArray();
        Assert.That(ContentHash.Compute(bytes), Is.EqualTo(ExpectedAbc));
        using var syncInput = new MemoryStream(bytes);
        Assert.That(ContentHash.Compute(syncInput), Is.EqualTo(ExpectedAbc));
        using var asyncInput = new MemoryStream(bytes);
        Assert.That(await ContentHash.ComputeAsync(asyncInput, CancellationToken.None), Is.EqualTo(ExpectedAbc));
        using var incremental = ContentHash.CreateIncremental();
        incremental.AppendData("a"u8);
        incremental.AppendData("bc"u8);
        Assert.That(ContentHash.Finish(incremental), Is.EqualTo(ExpectedAbc));
        using var algorithm = ContentHash.CreateAlgorithm();
        Assert.That(Convert.ToHexString(algorithm.ComputeHash(bytes)), Is.EqualTo(ExpectedAbc));
    }

    [Test]
    public void Derives_Digest_Validation_From_The_Selected_Algorithm()
    {
        Assert.That(ContentHash.DigestBytes * 2, Is.EqualTo(ExpectedAbc.Length));
        Assert.That(ContentHash.IsValid(ExpectedAbc), Is.True);
        Assert.That(ContentHash.IsValid(ExpectedAbc.ToLowerInvariant()), Is.True);
        Assert.That(ContentHash.IsValid(ExpectedAbc[..^1]), Is.False);
        Assert.That(ContentHash.IsValid(new string('Z', ExpectedAbc.Length)), Is.False);
        Assert.That(ContentHash.IsValid(null), Is.False);
        Assert.That(ContentHash.Format(Convert.FromHexString(ExpectedAbc)), Is.EqualTo(ExpectedAbc));
        Assert.Throws<InvalidDataException>(() => ContentHash.Format(new byte[ContentHash.DigestBytes + 1]));
    }

    [Test]
    public async Task Preserves_Caller_Cancellation_While_Hashing_A_Stream()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var input = new MemoryStream("abc"u8.ToArray());
        await Assert.CatchAsync<OperationCanceledException>(() => ContentHash.ComputeAsync(input, cancellation.Token));
    }

    [Test]
    public void Only_Enables_Device_Hashing_For_The_Afc_Algorithm()
    {
        Assert.That(ContentHash.SupportsAfcFileHash, Is.EqualTo(ContentHash.Algorithm == HashAlgorithmName.SHA1));
    }

    [Test]
    public void Reads_And_Writes_The_Existing_Hash_Field_For_Caches_And_Recovery()
    {
        var sourceJson = $$"""
            {"RelativePath":"song.mp3","Size":3,"LastWriteTimeUtc":"1970-01-01T00:00:00Z",
             "{{ContentHash.AlgorithmName}}":"{{ExpectedAbc}}"}
            """;

        var source = JsonSerializer.Deserialize<SourceTrack>(sourceJson)!;
        Assert.That(source.Hash, Is.EqualTo(ExpectedAbc));
        var entry = new DeviceHashEntry(
            1,
            "music/song.mp3",
            3,
            ExpectedAbc,
            new Dictionary<string, string>());

        var artwork = new JournalArtwork(1, 3, ExpectedAbc);
        AssertHashField(JsonSerializer.Serialize(source));
        AssertHashField(JsonSerializer.Serialize(entry));
        AssertHashField(JsonSerializer.Serialize(artwork));
        Assert.That(JsonSerializer.Deserialize<DeviceHashEntry>(JsonSerializer.Serialize(entry))!.Hash,
            Is.EqualTo(ExpectedAbc));

        Assert.That(JsonSerializer.Deserialize<JournalArtwork>(JsonSerializer.Serialize(artwork))!.Hash,
            Is.EqualTo(ExpectedAbc));
    }

    private static void AssertHashField(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty(ContentHash.AlgorithmName).GetString(), Is.EqualTo(ExpectedAbc));
        Assert.That(document.RootElement.TryGetProperty("Hash", out _), Is.False);
    }
}
