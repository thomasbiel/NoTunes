namespace NoTunes.Tests;

internal sealed class FailOnceEditStream(string path, Action onFirstWrite)
    : FileStream(
        path,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None)
{
    private bool injected;

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.BeforeWrite();
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.BeforeWrite();
        base.Write(buffer);
    }

    private void BeforeWrite()
    {
        if (this.injected) return;

        this.injected = true;
        base.WriteByte(0);
        onFirstWrite();
    }
}
