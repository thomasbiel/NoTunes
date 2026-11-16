using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NoTunes.Platform.Linux;

[SupportedOSPlatform("linux")]
internal sealed class LinuxSourceSession : ISourceSession
{
    private const int CloseOnExec = 1 << 19;
    private const int NonBlocking = 1 << 11;
    private const int EmptyPath = 0x1000;
    private const uint StatType = 1;
    private const int FileTypeMask = 0xf000;
    private const int RegularFile = 0x8000;
    private const int LockShared = 1;
    private const int LockNonBlocking = 4;
    private const string LibcName = "libc";
    private const string RootPath = "/";
    private const string FileDescriptorDirectory = "/proc/self/fd/";
    private readonly LinuxOpenFlags flags = LinuxOpenFlags.ForArchitecture(RuntimeInformation.ProcessArchitecture);
    private readonly Dictionary<string, SafeFileHandle> directories = new(StringComparer.Ordinal);

    private LinuxSourceSession()
    {
    }

    public static LinuxSourceSession Acquire(DirectoryInfo root, CancellationToken cancellationToken)
    {
        var session = new LinuxSourceSession();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = RootPath;
            var handle = CheckedHandle(Open(current, session.flags.Directory | session.flags.NoFollow | CloseOnExec), current);
            session.directories.Add(current, handle);
            // Open every component relative to its held parent; never resolve a mutable absolute path again.
            foreach (var component in root.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = Path.Combine(current, component);
                handle = session.OpenDirectory(handle, component, current);
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void AddDirectory(DirectoryInfo directory)
    {
        var parent = this.GetDirectory(directory.Parent ?? throw new IOException("A source subdirectory has no parent."));
        this.OpenDirectory(parent, directory.Name, directory.FullName);
    }

    private SafeFileHandle OpenDirectory(SafeFileHandle parent, string name, string path)
    {
        var handle = CheckedHandle(OpenAt(parent, name, this.flags.Directory | this.flags.NoFollow | CloseOnExec), path);
        try
        {
            this.directories.Add(path, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        var handle = this.GetDirectory(directory);
        // procfs exposes the held directory even if its original pathname has since been renamed.
        var descriptor = handle.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
        var heldPath = new DirectoryInfo(FileDescriptorDirectory + descriptor);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        };
        return heldPath.EnumerateFileSystemInfos(SourceFileDefaults.AllEntriesPattern, options).Select(entry => new SourceEntry(entry.Name, entry.Attributes));
    }

    public void ReleaseDirectory(DirectoryInfo directory)
    {
        if (this.directories.Remove(Path.TrimEndingDirectorySeparator(directory.FullName), out var handle))
            handle.Dispose();
    }

    public FileStream OpenRead(FileInfo file)
    {
        var parent = this.GetDirectory(file.Directory ?? throw new IOException("A source file has no parent directory."));
        // NONBLOCK also prevents a swapped-in FIFO from hanging the scan before its type is checked.
        var handle = CheckedHandle(OpenAt(parent, file.Name, this.flags.NoFollow | CloseOnExec | NonBlocking), file.FullName);
        try
        {
            if (Statx(
                handle,
                string.Empty,
                EmptyPath,
                StatType,
                out var status) != 0) throw NativeError(file.FullName);
            if ((status.Mask & StatType) == 0 || (status.Mode & FileTypeMask) != RegularFile)
                throw new IOException($"Source path is not a regular file: {file.FullName}");
            if (Flock(handle, LockShared | LockNonBlocking) != 0) throw NativeError(file.FullName);

            return new FileStream(
                handle,
                FileAccess.Read,
                SourceFileDefaults.ReadBufferBytes,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private SafeFileHandle GetDirectory(DirectoryInfo directory)
    {
        return this.directories.TryGetValue(Path.TrimEndingDirectorySeparator(directory.FullName), out var handle) ? handle : throw new IOException($"Source directory was not opened safely: {directory.FullName}");
    }

    private static SafeFileHandle CheckedHandle(int descriptor, string path)
    {
        return descriptor >= 0 ? new SafeFileHandle((IntPtr)descriptor, ownsHandle: true) : throw NativeError(path);
    }

    private static IOException NativeError(string path)
    {
        return new($"Cannot safely access source path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    public void Dispose()
    {
        foreach (var handle in this.directories.Values)
            handle.Dispose();
        this.directories.Clear();
    }

    [DllImport(LibcName, EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(LibcName, EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport(LibcName, EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatxStatus status);

    [DllImport(LibcName, EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle file, int operation);
}
