using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ArtworkCacheTests
{
    [Test]
    public void Reuses_Identical_Images_Without_Sharing_Track_Cache_Ids([Values] bool jpeg)
    {
        using var workspace = new SyncWorkspace();
        var cache = new ArtworkCache(workspace.Root);
        var source = jpeg ? ArtworkImage.Read(FeatureTestData.Png()).Bytes : FeatureTestData.Png();
        var first = cache.Read(source) with { CacheId = 101 };
        var second = cache.Read([.. source]) with { CacheId = 102 };
        Assert.That(first.LocalPath, Is.Not.Null);
        Assert.That(second.LocalPath, Is.EqualTo(first.LocalPath));
        Assert.That(second.Bytes, Is.EqualTo(first.Bytes));
        Assert.That(first.CacheId, Is.EqualTo(101));
        Assert.That(second.CacheId, Is.EqualTo(102));
        Assert.That(Directory.GetFiles(workspace.Root), Has.Length.EqualTo(1));
    }

    [Test]
    public void Evicts_Cache_Entries_Without_Deleting_Prepared_Images()
    {
        using var workspace = new SyncWorkspace();
        var cache = new ArtworkCache(workspace.Root, maximumEntries: 1);
        var source = FeatureTestData.Png();
        var first = cache.Read(source);
        // A valid PNG may have trailing bytes; the input hashes remain distinct.
        var second = cache.Read([.. source, 1]);
        var reloaded = cache.Read(source);
        Assert.That(reloaded.LocalPath, Is.Not.EqualTo(first.LocalPath));
        Assert.That(second.LocalPath, Is.Not.EqualTo(first.LocalPath));
        Assert.That(first.Bytes, Is.EqualTo(reloaded.Bytes));
        Assert.That(Directory.GetFiles(workspace.Root), Has.Length.EqualTo(3));
    }

    [Test]
    public void Rejects_Invalid_Covers_After_A_Valid_Cache_Entry()
    {
        using var workspace = new SyncWorkspace();
        var cache = new ArtworkCache(workspace.Root);
        cache.Read(FeatureTestData.Png());
        Assert.Throws<InvalidDataException>(() => cache.Read([1, 2, 3, 4]));
        Assert.That(Directory.GetFiles(workspace.Root), Has.Length.EqualTo(1));
    }

    [Test]
    public void Detects_Changes_To_Prepared_Image_Files([Values] bool changeSize)
    {
        using var workspace = new SyncWorkspace();
        var image = new ArtworkCache(workspace.Root).Read(FeatureTestData.Png());
        var bytes = image.Bytes;
        if (changeSize)
        {
            bytes = [.. bytes, 0];
        }
        else
        {
            bytes[^1] ^= 1;
        }

        File.WriteAllBytes(image.LocalPath!, bytes);
        Assert.Throws<IOException>(() => _ = image.Bytes);
    }

    [Test]
    public void Keeps_Metadata_Without_Loading_Or_Retaining_The_Prepared_Bytes()
    {
        using var workspace = new SyncWorkspace();
        var image = new ArtworkCache(workspace.Root).Read(FeatureTestData.Png());
        var length = image.Length;
        var hash = image.Sha256;
        File.Delete(image.LocalPath!);
        Assert.That(image.Length, Is.EqualTo(length));
        Assert.That(image.Checksum, Is.EqualTo(length + 1));
        Assert.That(image.Sha256, Is.EqualTo(hash));
        Assert.Throws<FileNotFoundException>(() => _ = image.Bytes);
    }
}
