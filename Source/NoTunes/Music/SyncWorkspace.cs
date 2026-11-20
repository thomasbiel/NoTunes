namespace NoTunes.Music;

internal sealed class SyncWorkspace : IDisposable
{
    private const string DirectoryPrefix = "NoTunes-";

    public string Root { get; }

    public SyncWorkspace(string? parent = null)
    {
        var name = DirectoryPrefix + Guid.NewGuid().ToString("N");
        this.Root = Path.GetFullPath(Path.Combine(parent ?? Path.GetTempPath(), name));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(this.Root);
        }
        else
        {
            var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(this.Root, privateMode);
        }
    }

    public bool Owns(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), this.Root, comparison);
    }

    public static void CleanAbandoned(string parent)
    {
        var root = Path.GetFullPath(parent);
        foreach (var directory in Directory.EnumerateDirectories(root, DirectoryPrefix + "*"))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _)) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;

            Directory.Delete(directory, recursive: true);
        }
    }

    public void Dispose()
    {
        Directory.Delete(this.Root, recursive: true);
    }
}
