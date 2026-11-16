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
        this.root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NoTunes-guard-tests-" + Guid.NewGuid().ToString("N")));
        this.source = Path.Combine(this.root, "parent", "source");
        this.outside = Path.Combine(this.root, "outside");
        Directory.CreateDirectory(this.source);
        Directory.CreateDirectory(this.outside);
        File.WriteAllText(Path.Combine(this.outside, "external.mp3"), "outside the source");
    }

    [Test]
    public void Pins_Root_And_Ancestors_Until_Disposed()
    {
        using (WindowsSourceSession.Acquire(new DirectoryInfo(this.source), CancellationToken.None))
        {
            Assert.That(() => this.MoveWithinFixture(this.source, this.source + "-moved"), Throws.InstanceOf<IOException>());
            var parent = Path.GetDirectoryName(this.source)!;
            Assert.That(() => this.MoveWithinFixture(parent, parent + "-moved"), Throws.InstanceOf<IOException>());
        }

        this.MoveWithinFixture(this.source, this.source + "-moved");
        Assert.That(Directory.Exists(this.source + "-moved"), Is.True);
    }

    [Test]
    public void Pins_Queued_Directories_And_Prevents_Conversion_To_A_Junction()
    {
        var child = Directory.CreateDirectory(Path.Combine(this.source, "queued"));
        using (var guard = WindowsSourceSession.Acquire(new DirectoryInfo(this.source), CancellationToken.None))
        {
            guard.AddDirectory(child);
            Assert.That(() => this.MoveWithinFixture(child.FullName, child.FullName + "-moved"), Throws.InstanceOf<IOException>());
            Assert.That(() => this.CreateJunction(child.FullName, this.outside), Throws.InstanceOf<IOException>());
        }

        this.MoveWithinFixture(child.FullName, child.FullName + "-moved");
    }

    [Test]
    public void Rejects_A_Junction_Inserted_After_Directory_Enumeration()
    {
        Directory.CreateDirectory(Path.Combine(this.source, "queued"));
        var cached = new DirectoryInfo(this.source).EnumerateDirectories().Single();
        Assert.That(cached.Attributes.HasFlag(FileAttributes.ReparsePoint), Is.False);
        this.MoveWithinFixture(cached.FullName, cached.FullName + "-original");
        this.CreateJunction(cached.FullName, this.outside);
        using var guard = WindowsSourceSession.Acquire(new DirectoryInfo(this.source), CancellationToken.None);
        Assert.That(() => guard.AddDirectory(cached), Throws.TypeOf<IOException>().With.Message.Contains("junction"));
    }

    [TestCase("")]
    [TestCase("nested")]
    public async Task Rejects_Junctions_At_Or_Above_The_Source_Root(string subfolder)
    {
        Directory.CreateDirectory(Path.Combine(this.outside, "nested"));
        var link = Path.Combine(this.root, "linked-source");
        this.CreateJunction(link, this.outside);
        var scanner = new SourceScanner(new WindowsSourceFileSystem());
        await Assert.ThatAsync(async () => await scanner.ScanAsync(Path.Combine(link, subfolder), CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("junction"));
    }

    [Test]
    public async Task Rejects_Junctions_Inside_The_Source()
    {
        this.CreateJunction(Path.Combine(this.source, "linked-album"), this.outside);
        var scanner = new SourceScanner(new WindowsSourceFileSystem());
        await Assert.ThatAsync(async () => await scanner.ScanAsync(this.source, CancellationToken.None), Throws.TypeOf<IOException>().With.Message.Contains("junction"));
    }

    [Test]
    public void Releases_Ancestor_Handles_When_Acquisition_Fails()
    {
        var link = Path.Combine(this.source, "linked-album");
        this.CreateJunction(link, this.outside);
        Assert.That(() => WindowsSourceSession.Acquire(new DirectoryInfo(link), CancellationToken.None), Throws.TypeOf<IOException>());
        var moved = this.source + "-moved";
        this.MoveWithinFixture(this.source, moved);
        this.junctions[0] = Path.Combine(moved, "linked-album");
    }

    private void MoveWithinFixture(string from, string to)
    {
        this.AssertWithinFixture(from);
        this.AssertWithinFixture(to);
        Directory.Move(from, to);
    }

    private void AssertWithinFixture(string path)
    {
        Assert.That(Path.GetFullPath(path).StartsWith(this.root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
    }

    private void CreateJunction(string path, string target)
    {
        this.AssertWithinFixture(path);
        this.AssertWithinFixture(target);
        Directory.CreateDirectory(path);
        using var handle = CreateFile(
            path,
            0x40000000,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0x02200000,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("Cannot open test junction.", new Win32Exception(Marshal.GetLastPInvokeError()));

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
        if (!DeviceIoControl(
            handle,
            0x000900a4,
            data,
            data.Length,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero)) // FSCTL_SET_REPARSE_POINT
            throw new IOException("Cannot create test junction.", new Win32Exception(Marshal.GetLastPInvokeError()));

        this.junctions.Add(path);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var junction in this.junctions)
        {
            this.AssertWithinFixture(junction);
            Directory.Delete(junction);
        }

        this.junctions.Clear();
        Assert.That(this.root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), Is.True);
        Directory.Delete(this.root, recursive: true);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[] input, int inputSize, IntPtr output, int outputSize, out uint bytesReturned, IntPtr overlapped);
}
