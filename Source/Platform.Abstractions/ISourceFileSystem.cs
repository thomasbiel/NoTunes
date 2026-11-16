namespace NoTunes.Platform;

public interface ISourceFileSystem
{
    ISourceSession OpenSource(DirectoryInfo root, CancellationToken cancellationToken);
}
