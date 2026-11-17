namespace NoTunes.Music;

internal sealed class SourceTagFile(Stream input) : TagLib.File.IFileAbstraction
{
    public string Name => "source.mp3";

    public Stream ReadStream { get; } = input;

    public Stream WriteStream => throw new NotSupportedException("Source metadata is read-only.");

    public void CloseStream(Stream stream)
    {
        // The source session owns this guarded file handle.
    }
}
