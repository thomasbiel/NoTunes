using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

internal sealed class BudgetSourceSession(DirectoryInfo rootDirectory, bool failRead) : ISourceSession
{
    private readonly HashSet<string> opened = new(StringComparer.Ordinal)
    {
        rootDirectory.FullName
    };

    public int MaximumDirectories { get; private set; } = 1;

    public int ActiveEnumerations { get; private set; }

    public bool Disposed { get; private set; }

    public IEnumerable<SourceEntry> EnumerateDirectory(DirectoryInfo directory)
    {
        Assert.That(this.opened, Does.Contain(directory.FullName));
        this.ActiveEnumerations++;
        try
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                yield return new SourceEntry(entry.Name, entry.Attributes);
            }
        }
        finally
        {
            this.ActiveEnumerations--;
        }
    }

    public void AddDirectory(DirectoryInfo directory)
    {
        this.opened.Add(directory.FullName);
        this.MaximumDirectories = Math.Max(this.MaximumDirectories, this.opened.Count);
        if (this.opened.Count > 8) throw new IOException("Directory handle budget exhausted.");
    }

    public void ReleaseDirectory(DirectoryInfo directory) => this.opened.Remove(directory.FullName);

    public FileStream OpenRead(FileInfo file)
    {
        Assert.That(this.opened, Does.Contain(file.Directory!.FullName));
        if (failRead) throw new IOException("Simulated read failure.");

        return file.OpenRead();
    }

    public void Dispose()
    {
        this.opened.Clear();
        this.Disposed = true;
    }
}
