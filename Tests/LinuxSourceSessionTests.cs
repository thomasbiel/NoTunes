using System.Runtime.Versioning;
using NoTunes.Platform.Linux;
using NUnit.Framework;

namespace NoTunes.Tests;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
public sealed class LinuxSourceSessionTests
{
    private string root = null!;
    private string source = null!;
    private string outside = null!;
    private readonly List<string> links = [];

    [SetUp]
    public void SetUp()
    {
        root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NoTunes-linux-tests-" + Guid.NewGuid().ToString("N")));
        source = Path.Combine(root, "source");
        outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "original.mp3"), "original");
        File.WriteAllText(Path.Combine(outside, "external.mp3"), "external");
    }

    [Test]
    public async Task DirectoryDescriptorsSurviveRenamesWithoutFollowingReplacementLinks()
    {
        using var session = LinuxSourceSession.Acquire(new DirectoryInfo(source), CancellationToken.None);
        MoveDirectory(source, source + "-moved");
        Directory.CreateSymbolicLink(source, outside);
        links.Add(source);
        var entries = session.EnumerateDirectory(new DirectoryInfo(source)).ToArray();
        Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "original.mp3" }));
        await using var stream = session.OpenRead(new FileInfo(Path.Combine(source, "original.mp3")));
        using var reader = new StreamReader(stream);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("original"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectsLinksInsertedAfterEnumeration(bool directory)
    {
        var path = Path.Combine(source, directory ? "album" : "original.mp3");
        if (directory) Directory.CreateDirectory(path);
        using var session = LinuxSourceSession.Acquire(new DirectoryInfo(source), CancellationToken.None);
        Assert.That(session.EnumerateDirectory(new DirectoryInfo(source)).Single(entry => entry.Name == Path.GetFileName(path))
            .Attributes.HasFlag(FileAttributes.ReparsePoint), Is.False);
        if (directory)
        {
            MoveDirectory(path, path + "-moved");
            Directory.CreateSymbolicLink(path, outside);
        }
        else
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, Path.Combine(outside, "external.mp3"));
        }
        links.Add(path);
        if (directory) Assert.That(() => session.AddDirectory(new DirectoryInfo(path)), Throws.TypeOf<IOException>());
        else Assert.That(() => session.OpenRead(new FileInfo(path)), Throws.TypeOf<IOException>());
    }

    [TestCase("")]
    [TestCase("nested")]
    public void RejectsLinksAtOrAboveTheRoot(string subfolder)
    {
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        var link = Path.Combine(root, "linked-source");
        Directory.CreateSymbolicLink(link, outside);
        links.Add(link);
        Assert.That(() => LinuxSourceSession.Acquire(new DirectoryInfo(Path.Combine(link, subfolder)), CancellationToken.None),
            Throws.TypeOf<IOException>());
    }

    private void MoveDirectory(string from, string to)
    {
        Assert.That(Path.GetFullPath(from).StartsWith(root + "/", StringComparison.Ordinal), Is.True);
        Assert.That(Path.GetFullPath(to).StartsWith(root + "/", StringComparison.Ordinal), Is.True);
        Directory.Move(from, to);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var link in links)
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            else File.Delete(link);
        }
        links.Clear();
        Assert.That(root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal), Is.True);
        Directory.Delete(root, recursive: true);
    }
}
