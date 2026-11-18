using NoTunes.Apple;
using NoTunes.Platform;

namespace NoTunes.Music;

internal sealed class SourceDirectoryLease(ISourceSession paths, string filePath) : IDisposable
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly List<DirectoryInfo> held = [];

    public string FilePath { get; } = filePath;

    public static SourceDirectoryLease Acquire(ISourceSession paths, string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison)) throw new IOException("Source track escapes the library root.");

        var lease = new SourceDirectoryLease(paths, fullPath);
        try
        {
            lease.HoldParents(root);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void HoldParents(string root)
    {
        var parts = Path.GetRelativePath(root, Path.GetDirectoryName(this.FilePath)!).Split(Path.DirectorySeparatorChar);
        var parent = root;
        foreach (var part in parts.Where(part => part != AfcKeys.CurrentDirectory))
        {
            parent = Path.Combine(parent, part);
            var directory = new DirectoryInfo(parent);
            paths.AddDirectory(directory);
            this.held.Add(directory);
        }
    }

    public void Dispose()
    {
        foreach (var directory in this.held.AsEnumerable().Reverse())
        {
            paths.ReleaseDirectory(directory);
        }
    }
}
