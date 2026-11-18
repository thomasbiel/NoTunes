using System.Security.Cryptography;
using System.Text.Json;
using NoTunes.Music;
using NoTunes.Platform;
using NoTunes.Platform.Linux;
using NoTunes.Platform.Windows;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SourceHashCacheTests
{
    private SyncWorkspace workspace = null!;
    private string source = null!;
    private string cacheDirectory = null!;
    private StringWriter error = null!;

    [SetUp]
    public void SetUp()
    {
        this.workspace = new SyncWorkspace();
        this.source = Path.Combine(this.workspace.Root, "source");
        this.cacheDirectory = Path.Combine(this.workspace.Root, "cache");
        this.error = new StringWriter();
        Directory.CreateDirectory(this.source);
    }

    [Test]
    public async Task Separates_Source_Roots_With_Identical_Relative_Paths_And_Metadata()
    {
        var other = Directory.CreateDirectory(Path.Combine(this.workspace.Root, "other")).FullName;
        var file = Path.Combine(this.source, "track.mp3");
        var otherFile = Path.Combine(other, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        await File.WriteAllTextAsync(otherFile, "xyz");
        File.SetLastWriteTimeUtc(otherFile, File.GetLastWriteTimeUtc(file));
        var first = await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        var second = await this.CreateScanner().ScanAsync(other, CancellationToken.None);
        Assert.That(second.Tracks[0].Sha256, Is.Not.EqualTo(first.Tracks[0].Sha256));
        Assert.That(Directory.GetFiles(this.cacheDirectory), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task Drops_Deleted_Files_And_Hashes_New_Files()
    {
        var oldFile = Path.Combine(this.source, "old.mp3");
        await File.WriteAllTextAsync(oldFile, "abc");
        await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        File.Delete(oldFile);
        await File.WriteAllTextAsync(Path.Combine(this.source, "new.mp3"), "xyz");
        var result = await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        var cached = this.CreateCache().Load(this.source);
        Assert.That(result.Tracks.Single().RelativePath, Is.EqualTo("new.mp3"));
        Assert.That(cached.Keys, Is.EquivalentTo(["new.mp3"]), this.error.ToString());
        Assert.That(cached["new.mp3"], Is.EqualTo(result.Tracks.Single()));
    }

    [TestCase("not JSON")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"Version\":999}")]
    public async Task Rebuilds_Unreadable_Or_Unsupported_Caches(string contents)
    {
        await File.WriteAllTextAsync(Path.Combine(this.source, "track.mp3"), "abc");
        Directory.CreateDirectory(this.cacheDirectory);
        await File.WriteAllTextAsync(this.CreateCache().PathFor(this.source), contents);
        var result = await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        Assert.That(result.Tracks.Single().Sha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData("abc"u8))));
        Assert.That(this.error.ToString(), Does.Contain("Source hash cache could not be read"));
        Assert.That(this.CreateCache().Load(this.source).Values, Is.EqualTo(result.Tracks));
        Assert.That(Directory.GetFiles(this.cacheDirectory, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task Rejects_Invalid_Cached_Hashes()
    {
        await File.WriteAllTextAsync(Path.Combine(this.source, "track.mp3"), "abc");
        var library = await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        var invalidTrack = library.Tracks.Single() with { Sha256 = "invalid" };
        await File.WriteAllTextAsync(this.CreateCache().PathFor(this.source), JsonSerializer.Serialize(
            new SourceHashSnapshot(1, this.source, [invalidTrack])));

        var result = await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        Assert.That(result.Tracks, Is.EqualTo(library.Tracks));
        Assert.That(this.error.ToString(), Does.Contain("Source hash cache could not be read"));
    }

    [Test]
    public async Task Cache_Write_Failure_Does_Not_Abort_The_Scan()
    {
        await File.WriteAllTextAsync(Path.Combine(this.source, "track.mp3"), "abc");
        await File.WriteAllTextAsync(this.cacheDirectory, "blocks cache directory creation");
        var result = await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        Assert.That(result.Tracks, Has.Count.EqualTo(1));
        Assert.That(this.error.ToString(), Does.Contain("Source hash cache could not be saved"));
    }

    [Test]
    public async Task Failed_Scans_Do_Not_Replace_The_Persisted_Inventory()
    {
        var file = Path.Combine(this.source, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        await this.CreateScanner().ScanAsync(this.source, CancellationToken.None);
        var path = this.CreateCache().PathFor(this.source);
        var before = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(file, "changed");
        var fileSystem = new MutatingSourceFileSystem(file, () => File.AppendAllText(file, "again"));
        var scanner = new SourceScanner(fileSystem, this.CreateCache());
        await Assert.ThrowsAsync<IOException>(() => scanner.ScanAsync(this.source, CancellationToken.None));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
    }

    private SourceScanner CreateScanner()
    {
        ISourceFileSystem fileSystem = OperatingSystem.IsWindows()
            ? new WindowsSourceFileSystem()
            : new LinuxSourceFileSystem();

        return new SourceScanner(fileSystem, this.CreateCache());
    }

    private SourceHashCache CreateCache()
    {
        return new SourceHashCache(this.cacheDirectory, this.error);
    }

    [TearDown]
    public void TearDown()
    {
        this.error.Dispose();
        this.workspace.Dispose();
    }
}
