namespace NoTunes.Platform;

public interface ISourceEditSession : ISourceSession
{
    FileStream OpenEdit(FileInfo file);
}
