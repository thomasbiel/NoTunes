using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SourceScannerTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "NoTunes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [Test]
    public async Task ScansNestedMixedCaseMp3FilesAndIgnoresOtherExtensions()
    {
        Directory.CreateDirectory(Path.Combine(root, "Album"));
        await File.WriteAllTextAsync(Path.Combine(root, "one.mp3"), "abc");
        await File.WriteAllBytesAsync(Path.Combine(root, "Album", "two.MP3"), [0, 1, 2]);
        await File.WriteAllTextAsync(Path.Combine(root, "cover.jpg"), "not music");
        await File.WriteAllTextAsync(Path.Combine(root, "fake.mp3.txt"), "not music");
        var library = await SourceScanner.ScanAsync(root, CancellationToken.None);
        Assert.That(library.Tracks, Has.Count.EqualTo(2));
        Assert.That(library.TotalBytes, Is.EqualTo(6));
        var track = library.Tracks.Single(t => t.RelativePath == "one.mp3");
        Assert.That(track.Sha256, Is.EqualTo("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.That(library.Tracks.Any(t => t.RelativePath == Path.Combine("Album", "two.MP3")), Is.True);
    }

    [Test]
    public async Task DetectsChangesEvenWhenSizeAndTimestampStayTheSame()
    {
        var file = Path.Combine(root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var before = await SourceScanner.ScanAsync(root, CancellationToken.None);
        await File.WriteAllTextAsync(file, "xyz");
        File.SetLastWriteTimeUtc(file, before.Tracks[0].LastWriteTimeUtc);
        var after = await SourceScanner.ScanAsync(root, CancellationToken.None);
        Assert.That(before.Tracks[0].Sha256, Is.Not.EqualTo(after.Tracks[0].Sha256));
    }

    [Test]
    public void MissingSourceNeverBecomesAnEmptyInventory() =>
        Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            SourceScanner.ScanAsync(Path.Combine(root, "missing"), CancellationToken.None));

    [Test]
    public void CancellationStopsBeforeReturningAnInventory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await SourceScanner.ScanAsync(root, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task LockedSourceFileStopsTheScan()
    {
        var file = Path.Combine(root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAsync<IOException>(() => SourceScanner.ScanAsync(root, CancellationToken.None));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);
}
