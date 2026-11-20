using System.Security.Cryptography;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class AfcHashTests
{
    private const string FilePath = "music/song.mp3";
    private AfcPeer peer = null!;
    private AfcClient afc = null!;

    [SetUp]
    public void SetUp()
    {
        this.peer = new AfcPeer();
        this.peer.Files[FilePath] = "abc"u8.ToArray();
        this.afc = new AfcClient(this.peer, TimeSpan.FromSeconds(2));
    }

    [TearDown]
    public async Task TearDown()
    {
        await this.afc.DisposeAsync();
    }

    [Test]
    public async Task Uses_The_Selected_Hash_And_Avoids_Readback_When_Afc_Is_Compatible()
    {
        var actual = await this.afc.HashFileAsync(FilePath, 3, CancellationToken.None);
        Assert.That(actual, Is.EqualTo(ContentHash.Compute("abc"u8)));
        Assert.That(this.peer.HashPaths.Count, Is.EqualTo(ContentHash.SupportsAfcFileHash ? 1 : 0));
        Assert.That(this.peer.Reads, Is.EqualTo(ContentHash.SupportsAfcFileHash ? 0 : 1));
        Assert.That(this.peer.Opens, Is.EqualTo(ContentHash.SupportsAfcFileHash ? 0 : 1));
        Assert.That(this.peer.Operations.Count(operation => operation == AfcOperation.GetFileInfo), Is.EqualTo(2));
    }

    [TestCase(6UL)]
    [TestCase(15UL)]
    public async Task Falls_Back_To_Readback_And_Remembers_Unsupported_Hashing(ulong status)
    {
        this.peer.FileHashError = (AfcStatusCode)status;
        var expected = Convert.ToHexString(SHA1.HashData("abc"u8));
        Assert.That(await this.HashAsync(), Is.EqualTo(expected));
        Assert.That(await this.HashAsync(), Is.EqualTo(expected));
        Assert.That(this.peer.HashPaths, Is.EqualTo([FilePath]));
        Assert.That(this.peer.ReadPaths, Is.EqualTo([FilePath, FilePath]));
        Assert.That(this.peer.OpenHandles, Is.Zero);
    }

    [Test]
    public async Task Explicit_Full_Readback_Bypasses_The_Device_Hash()
    {
        var actual = await this.HashAsync(fullReadback: true);
        Assert.That(actual, Is.EqualTo(ContentHash.Compute("abc"u8)));
        Assert.That(this.peer.HashPaths, Is.Empty);
        Assert.That(this.peer.ReadPaths, Is.EqualTo([FilePath]));
    }

    [Test]
    public async Task Rejects_An_Invalid_Digest_Without_Falling_Back([Values(0, 19, 21, 32)] int length)
    {
        this.peer.FileHashReply = new byte[length];
        await Assert.ThrowsAsync<InvalidDataException>(() => this.HashAsync());
        Assert.That(this.peer.ReadPaths, Is.Empty);
    }

    [TestCase(8UL)]
    [TestCase(10UL)]
    public async Task Preserves_Service_Errors_Without_Falling_Back(ulong status)
    {
        this.peer.FileHashError = (AfcStatusCode)status;
        var failure = await Assert.ThrowsAsync<AfcException>(() => this.HashAsync());
        Assert.That(failure!.Code, Is.EqualTo(status));
        Assert.That(this.peer.ReadPaths, Is.Empty);
    }

    [Test]
    public async Task Rejects_A_File_Changed_While_Hashing([Values] bool removed)
    {
        this.peer.OnFileHashed = path =>
        {
            if (removed)
            {
                this.peer.Files.Remove(path);
            }
            else
            {
                this.peer.FileInfoOverrides[path] = new() { [AfcKeys.ModificationTime] = "2" };
            }
        };

        await Assert.ThrowsAsync<IOException>(() => this.HashAsync());
        Assert.That(this.peer.ReadPaths, Is.Empty);
    }

    [Test]
    public async Task Rejects_A_Wrong_Size_Before_Requesting_A_Hash()
    {
        this.peer.FileInfoOverrides[FilePath] = new() { [AfcKeys.FileSize] = "4" };
        await Assert.ThrowsAsync<IOException>(() => this.HashAsync());
        Assert.That(this.peer.HashPaths, Is.Empty);
    }

    [TestCase("../song.mp3")]
    [TestCase("music/../song.mp3")]
    [TestCase("music/song\0.mp3")]
    public async Task Rejects_Unsafe_Paths_Before_Sending_A_Request(string path)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            this.afc.HashFileAsync(path, 3, CancellationToken.None));

        Assert.That(this.peer.Operations, Is.Empty);
    }

    [Test]
    public async Task Cancellation_Closes_The_Connection_Without_Falling_Back()
    {
        using var cancellation = new CancellationTokenSource();
        var peer = new AfcPeer
        {
            BeforeFileHashReplyAsync = token =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        peer.Files[FilePath] = "abc"u8.ToArray();
        await using var afc = new AfcClient(peer, TimeSpan.FromSeconds(2));
        await Assert.CatchAsync<OperationCanceledException>(() => afc.HashFileAsync(FilePath, 3, cancellation.Token));
        Assert.That(afc.IsBroken, Is.True);
        Assert.That(peer.ReadPaths, Is.Empty);
    }

    private Task<string> HashAsync(bool fullReadback = false)
    {
        return this.afc.HashFileAsync(
            FilePath,
            3,
            CancellationToken.None,
            fullReadback: fullReadback);
    }
}
