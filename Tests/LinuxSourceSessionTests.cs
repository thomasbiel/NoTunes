using System.Runtime.Versioning;
using NoTunes.Music;
using NoTunes.Platform.Linux;
using NUnit.Framework;

namespace NoTunes.Tests;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
[TestFixture]
public sealed class LinuxSourceSessionTests
{
    private string root = null!;
    private string source = null!;
    private string outside = null!;
    private readonly List<string> links = [];

    [SetUp]
    public void SetUp()
    {
        this.root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NoTunes-linux-tests-" + Guid.NewGuid().ToString("N")));
        this.source = Path.Combine(this.root, "source");
        this.outside = Path.Combine(this.root, "outside");
        Directory.CreateDirectory(this.source);
        Directory.CreateDirectory(this.outside);
        File.WriteAllText(Path.Combine(this.source, "original.mp3"), "original");
        File.WriteAllText(Path.Combine(this.outside, "external.mp3"), "external");
    }

    [Test]
    public async Task Directory_Descriptors_Survive_Renames_Without_Following_Replacement_Links()
    {
        using var session = LinuxSourceSession.Acquire(new DirectoryInfo(this.source), CancellationToken.None);
        this.MoveDirectory(this.source, this.source + "-moved");
        Directory.CreateSymbolicLink(this.source, this.outside);
        this.links.Add(this.source);
        var entries = session.EnumerateDirectory(new DirectoryInfo(this.source)).ToArray();
        Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(["original.mp3"]));
        await using var stream = session.OpenRead(new FileInfo(Path.Combine(this.source, "original.mp3")));
        using var reader = new StreamReader(stream);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("original"));
    }

    [Test]
    public void Rejects_Links_Inserted_After_Enumeration([Values] bool directory)
    {
        var path = Path.Combine(this.source, directory ? "album" : "original.mp3");
        if (directory)
            Directory.CreateDirectory(path);
        using var session = LinuxSourceSession.Acquire(new DirectoryInfo(this.source), CancellationToken.None);
        Assert.That(session.EnumerateDirectory(new DirectoryInfo(this.source)).Single(entry => entry.Name == Path.GetFileName(path)).Attributes.HasFlag(FileAttributes.ReparsePoint), Is.False);
        if (directory)
        {
            this.MoveDirectory(path, path + "-moved");
            Directory.CreateSymbolicLink(path, this.outside);
        }

        else
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, Path.Combine(this.outside, "external.mp3"));
        }

        this.links.Add(path);
        if (directory)
            Assert.That(() => session.AddDirectory(new DirectoryInfo(path)), Throws.TypeOf<IOException>());
        else
            Assert.That(() => session.OpenRead(new FileInfo(path)), Throws.TypeOf<IOException>());
    }

    [TestCase("")]
    [TestCase("nested")]
    public void Rejects_Links_At_Or_Above_The_Root(string subfolder)
    {
        Directory.CreateDirectory(Path.Combine(this.outside, "nested"));
        var link = Path.Combine(this.root, "linked-source");
        Directory.CreateSymbolicLink(link, this.outside);
        this.links.Add(link);
        Assert.That(() => LinuxSourceSession.Acquire(new DirectoryInfo(Path.Combine(link, subfolder)), CancellationToken.None), Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task Wide_Scans_Keep_Native_Descriptor_Usage_Bounded()
    {
        for (var i = 0; i < 256; i++)
        {
            var album = Directory.CreateDirectory(Path.Combine(this.source, $"album-{i}"));
            await File.WriteAllTextAsync(Path.Combine(album.FullName, "track.mp3"), "abc");
        }

        var before = Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();
        var maximum = before;
        var fileSystem = new ObservedSourceFileSystem(() => maximum = Math.Max(maximum, Directory.EnumerateFileSystemEntries("/proc/self/fd").Count()));
        var result = await new SourceScanner(fileSystem).ScanAsync(this.source, CancellationToken.None);
        Assert.That(result.Tracks, Has.Count.EqualTo(257));
        Assert.That(maximum - before, Is.LessThan(32), "Descriptor usage must not grow with the number of albums.");
        Assert.That(Directory.EnumerateFileSystemEntries("/proc/self/fd").Count() - before, Is.LessThan(8));
    }

    private void MoveDirectory(string from, string to)
    {
        Assert.That(Path.GetFullPath(from).StartsWith(this.root + "/", StringComparison.Ordinal), Is.True);
        Assert.That(Path.GetFullPath(to).StartsWith(this.root + "/", StringComparison.Ordinal), Is.True);
        Directory.Move(from, to);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var link in this.links)
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            else
                File.Delete(link);
        }

        this.links.Clear();
        Assert.That(this.root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal), Is.True);
        Directory.Delete(this.root, recursive: true);
    }
}
