namespace NoTunes.Music;

internal sealed class SourceTagFile(Stream input, string name = "source.mp3", bool writable = false)
    : TagLib.File.IFileAbstraction
{
    public string Name => name;

    public Stream ReadStream { get; } = input;

    public Stream WriteStream => writable ? this.ReadStream : throw new NotSupportedException("Source metadata is read-only.");

    public void CloseStream(Stream stream)
    {
        // The caller owns the guarded stream.
    }
}
