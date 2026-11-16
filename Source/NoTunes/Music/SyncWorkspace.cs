namespace NoTunes.Music;

internal sealed class SyncWorkspace : IDisposable
{
    private const string DirectoryPrefix = "NoTunes-";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), DirectoryPrefix + Guid.NewGuid().ToString("N"));

    public SyncWorkspace()
    {
        Directory.CreateDirectory(this.Root);
    }

    public void Dispose()
    {
        Directory.Delete(this.Root, recursive: true);
    }
}
