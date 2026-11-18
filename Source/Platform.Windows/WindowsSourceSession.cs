using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NoTunes.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSourceSession : ISourceEditSession
{
    private const uint ReadAttributes = 0x80;
    private const uint ListDirectory = 0x1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint SequentialScan = 0x08000000;
    private const uint Overlapped = 0x40000000;

    // Pin different spellings separately because Windows directories can enable case-sensitive names.
    private readonly Dictionary<string, SafeFileHandle> directories = new(StringComparer.Ordinal);

    private WindowsSourceSession() { }

    public static WindowsSourceSession Acquire(DirectoryInfo root, CancellationToken cancellationToken)
    {
        var guard = new WindowsSourceSession();
        try
        {
            // Pin ancestors first: OPEN_REPARSE_POINT only protects the last path component.
            var ancestors = new Stack<DirectoryInfo>();
            for (var directory = root; directory is not null; directory = directory.Parent)
            {
                ancestors.Push(directory);
            }

            while (ancestors.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                guard.AddDirectory(directory);
            }

            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    // Attribute-only handles do not enforce Windows sharing restrictions.
    public void AddDirectory(DirectoryInfo directory)
    {
        var path = Path.TrimEndingDirectorySeparator(directory.FullName);
        if (this.directories.ContainsKey(path)) return;

        var handle = Open(
            path,
            ReadAttributes | ListDirectory,
            BackupSemantics,
            directory: true);

        this.directories.Add(path, handle);
    }

    // Windows must keep sharing guards until the complete inventory is finished.
    public void ReleaseDirectory(DirectoryInfo directory) { }

    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        };

        return directory.EnumerateFileSystemInfos(SourceFileDefaults.AllEntriesPattern, options)
                        .Select(entry => new SourceEntry(entry.Name, entry.Attributes));
    }

    public FileStream OpenRead(FileInfo file) => OpenFile(file, FileAccess.Read);

    public FileStream OpenEdit(FileInfo file) => OpenFile(file, FileAccess.ReadWrite);

    private static FileStream OpenFile(FileInfo file, FileAccess access)
    {
        var handle = Open(
            file.FullName,
            access == FileAccess.ReadWrite ? GenericRead | GenericWrite : GenericRead,
            Overlapped | SequentialScan,
            directory: false,
            access == FileAccess.ReadWrite ? FileShare.None : FileShare.Read);

        try
        {
            return new FileStream(
                handle,
                access,
                SourceFileDefaults.ReadBufferBytes,
                isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint flags, bool directory, FileShare share = FileShare.Read)
    {
        // Deny write/delete sharing so a validated component cannot become a link or be replaced.
        var handle = CreateFile(
            Path.TrimEndingDirectorySeparator(path),
            access,
            share,
            IntPtr.Zero,
            FileMode.Open,
            flags | OpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw new IOException($"Cannot safely open source path '{path}': {error.Message}", error);
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Symbolic link or junction in source path: {path}. The scan is aborted.");
            if (attributes.HasFlag(FileAttributes.Directory) != directory) throw new IOException($"Source path changed type during the scan: {path}");

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var handle in this.directories.Values.Reverse())
        {
            handle.Dispose();
        }

        this.directories.Clear();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
