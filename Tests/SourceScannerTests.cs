using NoTunes.Music;
using NoTunes.Platform;
using NoTunes.Platform.Linux;
using NoTunes.Platform.Windows;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SourceScannerTests
{
    private string root = null!;
    private SourceScanner scanner = null!;

    [SetUp]
    public void SetUp()
    {
        this.root = Path.Combine(Path.GetTempPath(), "NoTunes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.root);
        ISourceFileSystem fileSystem = OperatingSystem.IsWindows() ? new WindowsSourceFileSystem() : new LinuxSourceFileSystem();
        this.scanner = new SourceScanner(fileSystem);
    }

    [Test]
    public async Task Scans_Nested_Mixed_Case_Mp3_Files_And_Ignores_Other_Extensions()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "Album"));
        await File.WriteAllTextAsync(Path.Combine(this.root, "one.mp3"), "abc");
        await File.WriteAllBytesAsync(Path.Combine(this.root, "Album", "two.MP3"), [0, 1, 2]);
        await File.WriteAllTextAsync(Path.Combine(this.root, "cover.jpg"), "not music");
        await File.WriteAllTextAsync(Path.Combine(this.root, "fake.mp3.txt"), "not music");
        var library = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        Assert.That(library.Tracks, Has.Count.EqualTo(2));
        Assert.That(library.TotalBytes, Is.EqualTo(6));
        var track = library.Tracks.Single(t => t.RelativePath == "one.mp3");
        Assert.That(track.Sha256, Is.EqualTo("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.That(library.Tracks.Any(t => t.RelativePath == Path.Combine("Album", "two.MP3")), Is.True);
    }

    [Test]
    public async Task Hashes_Content_Changes_Between_Scans_Even_When_Size_And_Timestamp_Stay_The_Same()
    {
        var file = Path.Combine(this.root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var before = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        await File.WriteAllTextAsync(file, "xyz");
        File.SetLastWriteTimeUtc(file, before.Tracks[0].LastWriteTimeUtc);
        var after = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        Assert.That(before.Tracks[0].Sha256, Is.Not.EqualTo(after.Tracks[0].Sha256));
    }

    [Test]
    public async Task Rejects_Files_Changed_During_Hashing([Values] bool changeLength)
    {
        var file = Path.Combine(this.root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var originalWriteTime = File.GetLastWriteTimeUtc(file);
        var fileSystem = new MutatingSourceFileSystem(file, () =>
        {
            if (changeLength)
            {
                File.AppendAllText(file, "d");
                File.SetLastWriteTimeUtc(file, originalWriteTime);
            }

            else
            {
                File.WriteAllText(file, "xyz");
                File.SetLastWriteTimeUtc(file, originalWriteTime.AddMinutes(1));
            }
        });
        var concurrentScanner = new SourceScanner(fileSystem);
        await Assert.ThatAsync(async () => await concurrentScanner.ScanAsync(this.root, CancellationToken.None), Throws.TypeOf<IOException>().With.Message.StartsWith("File changed during the scan:"));
        Assert.That(fileSystem.Session.OpenedStream?.MutationPerformed, Is.True);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Missing_Source_Never_Becomes_An_Empty_Inventory()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => this.scanner.ScanAsync(Path.Combine(this.root, "missing"), CancellationToken.None));
    }

    [Test]
    public void Cancellation_Stops_Before_Returning_An_Inventory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await this.scanner.ScanAsync(this.root, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Locked_Source_File_Stops_The_Scan()
    {
        var file = Path.Combine(this.root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        await using var exclusive = new FileStream(
            file,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => this.scanner.ScanAsync(this.root, CancellationToken.None));
    }

    [Test]
    public async Task Scans_Wide_Libraries_Within_A_Directory_Handle_Budget()
    {
        for (var i = 0; i < 128; i++)
        {
            var album = Directory.CreateDirectory(Path.Combine(this.root, $"album-{i}"));
            await File.WriteAllTextAsync(Path.Combine(album.FullName, "track.mp3"), "abc");
        }

        var fileSystem = new BudgetSourceFileSystem();
        var result = await new SourceScanner(fileSystem).ScanAsync(this.root, CancellationToken.None);
        Assert.That(result.Tracks, Has.Count.EqualTo(128));
        Assert.That(fileSystem.Session.MaximumDirectories, Is.LessThanOrEqualTo(2));
        Assert.That(fileSystem.Session.ActiveEnumerations, Is.Zero);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Disposes_Active_Directory_Enumerators_After_A_Read_Failure()
    {
        var album = Directory.CreateDirectory(Path.Combine(this.root, "album"));
        await File.WriteAllTextAsync(Path.Combine(album.FullName, "track.mp3"), "abc");
        var fileSystem = new BudgetSourceFileSystem
        {
            FailRead = true
        };
        await Assert.ThrowsAsync<IOException>(() => new SourceScanner(fileSystem).ScanAsync(this.root, CancellationToken.None));
        Assert.That(fileSystem.Session.ActiveEnumerations, Is.Zero);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(this.root, recursive: true);
    }
}
