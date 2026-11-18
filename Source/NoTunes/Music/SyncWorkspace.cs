namespace NoTunes.Music;

internal sealed class SyncWorkspace : IDisposable
{
    private const string DirectoryPrefix = "NoTunes-";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), DirectoryPrefix + Guid.NewGuid().ToString("N"));

    public SyncWorkspace()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(this.Root);
        }
        else
        {
            Directory.CreateDirectory(this.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        Directory.Delete(this.Root, recursive: true);
    }
}
