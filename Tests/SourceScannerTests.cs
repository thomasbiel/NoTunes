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
    private string cacheDirectory = null!;
    private SourceScanner scanner = null!;
    private StringWriter error = null!;

    [SetUp]
    public void SetUp()
    {
        this.root = Path.Combine(Path.GetTempPath(), "NoTunes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.root);
        this.cacheDirectory = Path.Combine(this.root, "cache");
        ISourceFileSystem fileSystem = OperatingSystem.IsWindows() ? new WindowsSourceFileSystem() : new LinuxSourceFileSystem();
        this.error = new StringWriter();
        this.scanner = new SourceScanner(fileSystem, new SourceHashCache(this.cacheDirectory, TextWriter.Null), this.error);
    }

    [Test]
    public async Task Rejects_Inventory_Changes_Between_Counting_And_Hashing([Values] bool added)
    {
        var path = Path.Combine(this.root, "old.mp3");
        await File.WriteAllTextAsync(path, "abc");
        var enumerations = 0;
        var fileSystem = new BudgetSourceFileSystem
        {
            BeforeEnumeration = () =>
            {
                if (++enumerations != 2) return;

                if (added)
                {
                    File.WriteAllText(Path.Combine(this.root, "new.mp3"), "xyz");
                }
                else
                {
                    File.Delete(path);
                }
            }
        };

        var scanner = new SourceScanner(fileSystem);
        await Assert.ThatAsync(() => scanner.ScanAsync(this.root, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("inventory changed"));

        Assert.That(fileSystem.Session.ActiveEnumerations, Is.Zero);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Reports_Progress_For_Nested_Files_Including_Cache_Hits([Values] bool cached)
    {
        Directory.CreateDirectory(Path.Combine(this.root, "album"));
        await File.WriteAllTextAsync(Path.Combine(this.root, "one.mp3"), "abc");
        await File.WriteAllTextAsync(Path.Combine(this.root, "album", "two.mp3"), "xyz");
        if (cached)
        {
            await this.scanner.ScanAsync(this.root, CancellationToken.None);
            this.error.GetStringBuilder().Clear();
        }

        var result = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        var diagnostics = this.error.ToString();
        Assert.That(result.Tracks, Has.Count.EqualTo(2));
        Assert.That(diagnostics, Does.Contain("Scanning and hashing source: 0% (0/2)"));
        Assert.That(diagnostics, Does.Contain("Scanning and hashing source: 50% (1/2)"));
        Assert.That(diagnostics, Does.Contain("Scanning and hashing source: 100% (2/2)"));
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
        Assert.That(track.Hash, Is.EqualTo("A9993E364706816ABA3E25717850C26C9CD0D89D"));
        Assert.That(library.Tracks.Any(t => t.RelativePath == Path.Combine("Album", "two.MP3")), Is.True);
    }

    [Test]
    public async Task Reuses_Persisted_Hashes_Without_Reading_Unchanged_File_Contents()
    {
        var file = Path.Combine(this.root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var before = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        var fileSystem = new MutatingSourceFileSystem(file, () => Assert.Fail("Cached contents must not be read."));
        var scanner = new SourceScanner(fileSystem, new SourceHashCache(this.cacheDirectory, TextWriter.Null));
        var after = await scanner.ScanAsync(this.root + Path.DirectorySeparatorChar, CancellationToken.None);
        Assert.That(after.Tracks, Is.EqualTo(before.Tracks));
        Assert.That(fileSystem.Session.OpenedStream, Is.Not.Null, "File guards must also run on cache hits.");
        Assert.That(fileSystem.Session.OpenedStream!.MutationPerformed, Is.False);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Rehashes_When_Size_Or_Timestamp_Changes([Values] bool changeLength)
    {
        var file = Path.Combine(this.root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var before = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        await File.WriteAllTextAsync(file, changeLength ? "abcd" : "xyz");
        File.SetLastWriteTimeUtc(file, changeLength
            ? before.Tracks[0].LastWriteTimeUtc
            : before.Tracks[0].LastWriteTimeUtc.AddMinutes(1));

        var after = await this.scanner.ScanAsync(this.root, CancellationToken.None);
        Assert.That(before.Tracks[0].Hash, Is.Not.EqualTo(after.Tracks[0].Hash));
        Assert.That(after.Tracks[0].Hash, Is.EqualTo(Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(await File.ReadAllBytesAsync(file)))));
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
        await Assert.ThatAsync(async () => await concurrentScanner.ScanAsync(this.root, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.StartsWith("File changed during the scan:"));

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
    public async Task Cached_Files_Still_Require_Access_Through_The_File_Guards()
    {
        var file = Path.Combine(this.root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        await this.scanner.ScanAsync(this.root, CancellationToken.None);
        var scanner = new SourceScanner(
            new BudgetSourceFileSystem { FailRead = true },
            new SourceHashCache(this.cacheDirectory, TextWriter.Null));

        await Assert.ThrowsAsync<IOException>(() => scanner.ScanAsync(this.root, CancellationToken.None));
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
        var fileSystem = new BudgetSourceFileSystem { FailRead = true };
        await Assert.ThrowsAsync<IOException>(() => new SourceScanner(fileSystem).ScanAsync(this.root, CancellationToken.None));
        Assert.That(fileSystem.Session.ActiveEnumerations, Is.Zero);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        this.error.Dispose();
        Directory.Delete(this.root, recursive: true);
    }
}
