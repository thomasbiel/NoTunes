using NoTunes.Music;
using NoTunes.Platform;
using NoTunes.Platform.Linux;
using NoTunes.Platform.Windows;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SourceScannerTests
{
    private string root = null!;
    private SourceScanner scanner = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "NoTunes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ISourceFileSystem fileSystem = OperatingSystem.IsWindows() ? new WindowsSourceFileSystem() : new LinuxSourceFileSystem();
        scanner = new SourceScanner(fileSystem);
    }

    [Test]
    public async Task ScansNestedMixedCaseMp3FilesAndIgnoresOtherExtensions()
    {
        Directory.CreateDirectory(Path.Combine(root, "Album"));
        await File.WriteAllTextAsync(Path.Combine(root, "one.mp3"), "abc");
        await File.WriteAllBytesAsync(Path.Combine(root, "Album", "two.MP3"), [0, 1, 2]);
        await File.WriteAllTextAsync(Path.Combine(root, "cover.jpg"), "not music");
        await File.WriteAllTextAsync(Path.Combine(root, "fake.mp3.txt"), "not music");
        var library = await scanner.ScanAsync(root, CancellationToken.None);
        Assert.That(library.Tracks, Has.Count.EqualTo(2));
        Assert.That(library.TotalBytes, Is.EqualTo(6));
        var track = library.Tracks.Single(t => t.RelativePath == "one.mp3");
        Assert.That(track.Sha256, Is.EqualTo("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.That(library.Tracks.Any(t => t.RelativePath == Path.Combine("Album", "two.MP3")), Is.True);
    }

    [Test]
    public async Task HashesContentChangesBetweenScansEvenWhenSizeAndTimestampStayTheSame()
    {
        var file = Path.Combine(root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var before = await scanner.ScanAsync(root, CancellationToken.None);
        await File.WriteAllTextAsync(file, "xyz");
        File.SetLastWriteTimeUtc(file, before.Tracks[0].LastWriteTimeUtc);
        var after = await scanner.ScanAsync(root, CancellationToken.None);
        Assert.That(before.Tracks[0].Sha256, Is.Not.EqualTo(after.Tracks[0].Sha256));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RejectsFilesChangedDuringHashing(bool changeLength)
    {
        var file = Path.Combine(root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        var originalWriteTime = File.GetLastWriteTimeUtc(file);
        var fileSystem = new MutatingFileSystem(file, () =>
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
        await Assert.ThatAsync(async () => await concurrentScanner.ScanAsync(root, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.StartsWith("File changed during the scan:"));
        Assert.That(fileSystem.Session.OpenedStream?.MutationPerformed, Is.True);
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [Test]
    public void MissingSourceNeverBecomesAnEmptyInventory() =>
        Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            scanner.ScanAsync(Path.Combine(root, "missing"), CancellationToken.None));

    [Test]
    public void CancellationStopsBeforeReturningAnInventory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await scanner.ScanAsync(root, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task LockedSourceFileStopsTheScan()
    {
        var file = Path.Combine(root, "track.mp3");
        await File.WriteAllTextAsync(file, "abc");
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAsync<IOException>(() => scanner.ScanAsync(root, CancellationToken.None));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private sealed class MutatingFileSystem(string file, Action mutate) : ISourceFileSystem
    {
        public MutatingSession Session { get; } = new(file, mutate);
        public ISourceSession OpenSource(DirectoryInfo directory, CancellationToken cancellationToken) => Session;
    }

    private sealed class MutatingSession(string file, Action mutate) : ISourceSession
    {
        public MutatingFileStream? OpenedStream { get; private set; }
        public bool Disposed { get; private set; }
        public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory) =>
            [new(Path.GetFileName(file), FileAttributes.Normal)];
        public void AddDirectory(DirectoryInfo directory) => throw new NotSupportedException();
        public FileStream OpenRead(FileInfo info) => OpenedStream = new MutatingFileStream(file, mutate);
        public void Dispose() => Disposed = true;
    }

    private sealed class MutatingFileStream(string file, Action mutate)
        : FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.Asynchronous)
    {
        public bool MutationPerformed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await RandomAccess.ReadAsync(SafeFileHandle, buffer[..Math.Min(buffer.Length, 1)], Position, cancellationToken);
            Position += read;
            if (read > 0 && !MutationPerformed)
            {
                MutationPerformed = true;
                mutate();
            }
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
