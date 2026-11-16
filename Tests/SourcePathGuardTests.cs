using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NoTunes.Music;
using NoTunes.Platform.Windows;
using NUnit.Framework;

namespace NoTunes.Tests;

[Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class SourcePathGuardTests
{
    private string root = null!;
    private string source = null!;
    private string outside = null!;
    private readonly List<string> junctions = [];

    [SetUp]
    public void SetUp()
    {
        root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NoTunes-guard-tests-" + Guid.NewGuid().ToString("N")));
        source = Path.Combine(root, "parent", "source");
        outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "external.mp3"), "outside the source");
    }

    [Test]
    public void PinsRootAndAncestorsUntilDisposed()
    {
        using (WindowsSourceSession.Acquire(new DirectoryInfo(source), CancellationToken.None))
        {
            Assert.That(() => MoveWithinFixture(source, source + "-moved"), Throws.InstanceOf<IOException>());
            var parent = Path.GetDirectoryName(source)!;
            Assert.That(() => MoveWithinFixture(parent, parent + "-moved"), Throws.InstanceOf<IOException>());
        }
        MoveWithinFixture(source, source + "-moved");
        Assert.That(Directory.Exists(source + "-moved"), Is.True);
    }

    [Test]
    public void PinsQueuedDirectoriesAndPreventsConversionToAJunction()
    {
        var child = Directory.CreateDirectory(Path.Combine(source, "queued"));
        using (var guard = WindowsSourceSession.Acquire(new DirectoryInfo(source), CancellationToken.None))
        {
            guard.AddDirectory(child);
            Assert.That(() => MoveWithinFixture(child.FullName, child.FullName + "-moved"), Throws.InstanceOf<IOException>());
            Assert.That(() => CreateJunction(child.FullName, outside), Throws.InstanceOf<IOException>());
        }
        MoveWithinFixture(child.FullName, child.FullName + "-moved");
    }

    [Test]
    public void RejectsAJunctionInsertedAfterDirectoryEnumeration()
    {
        Directory.CreateDirectory(Path.Combine(source, "queued"));
        var cached = new DirectoryInfo(source).EnumerateDirectories().Single();
        Assert.That(cached.Attributes.HasFlag(FileAttributes.ReparsePoint), Is.False);
        MoveWithinFixture(cached.FullName, cached.FullName + "-original");
        CreateJunction(cached.FullName, outside);
        using var guard = WindowsSourceSession.Acquire(new DirectoryInfo(source), CancellationToken.None);
        Assert.That(() => guard.AddDirectory(cached),
            Throws.TypeOf<IOException>().With.Message.Contains("junction"));
    }

    [TestCase("")]
    [TestCase("nested")]
    public async Task RejectsJunctionsAtOrAboveTheSourceRoot(string subfolder)
    {
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        var link = Path.Combine(root, "linked-source");
        CreateJunction(link, outside);
        var scanner = new SourceScanner(new WindowsSourceFileSystem());
        await Assert.ThatAsync(async () => await scanner.ScanAsync(Path.Combine(link, subfolder), CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("junction"));
    }

    [Test]
    public async Task RejectsJunctionsInsideTheSource()
    {
        CreateJunction(Path.Combine(source, "linked-album"), outside);
        var scanner = new SourceScanner(new WindowsSourceFileSystem());
        await Assert.ThatAsync(async () => await scanner.ScanAsync(source, CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("junction"));
    }

    [Test]
    public void ReleasesAncestorHandlesWhenAcquisitionFails()
    {
        var link = Path.Combine(source, "linked-album");
        CreateJunction(link, outside);
        Assert.That(() => WindowsSourceSession.Acquire(new DirectoryInfo(link), CancellationToken.None),
            Throws.TypeOf<IOException>());
        var moved = source + "-moved";
        MoveWithinFixture(source, moved);
        junctions[0] = Path.Combine(moved, "linked-album");
    }

    private void MoveWithinFixture(string from, string to)
    {
        AssertWithinFixture(from);
        AssertWithinFixture(to);
        Directory.Move(from, to);
    }

    private void AssertWithinFixture(string path) =>
        Assert.That(Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);

    private void CreateJunction(string path, string target)
    {
        AssertWithinFixture(path);
        AssertWithinFixture(target);
        Directory.CreateDirectory(path);
        using var handle = CreateFile(path, 0x40000000, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot open test junction.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var printName = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + printName.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xa0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)printName.Length);
        substitute.CopyTo(data, 16);
        printName.CopyTo(data, 16 + substitute.Length + 2);
        if (!DeviceIoControl(handle, 0x000900a4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) // FSCTL_SET_REPARSE_POINT
            throw new IOException("Cannot create test junction.", new Win32Exception(Marshal.GetLastPInvokeError()));
        junctions.Add(path);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var junction in junctions)
        {
            AssertWithinFixture(junction);
            Directory.Delete(junction);
        }
        junctions.Clear();
        Assert.That(root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), Is.True);
        Directory.Delete(root, recursive: true);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[] input, int inputSize,
        IntPtr output, int outputSize, out uint bytesReturned, IntPtr overlapped);
}
