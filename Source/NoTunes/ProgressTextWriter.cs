using System.Text;

namespace NoTunes;

internal sealed class ProgressTextWriter(TextWriter output, bool interactive) : TextWriter
{
    private int progressWidth;

    public override Encoding Encoding => output.Encoding;

    public void WriteProgress(string message, bool complete)
    {
        if (!interactive)
        {
            output.WriteLine(message);
            return;
        }

        output.Write('\r');
        output.Write(message);
        if (this.progressWidth > message.Length)
        {
            output.Write(new string(' ', this.progressWidth - message.Length));
        }

        this.progressWidth = message.Length;
        if (complete) this.FinishProgress();

        output.Flush();
    }

    public override void Write(char value)
    {
        this.FinishProgress();
        output.Write(value);
    }

    public override void Write(string? value)
    {
        this.FinishProgress();
        output.Write(value);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        this.FinishProgress();
        output.Write(buffer);
    }

    public override void WriteLine(string? value)
    {
        this.FinishProgress();
        output.WriteLine(value);
    }

    public override Task WriteAsync(string? value)
    {
        this.FinishProgress();
        return output.WriteAsync(value);
    }

    public override Task WriteLineAsync(string? value)
    {
        this.FinishProgress();
        return output.WriteLineAsync(value);
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.FinishProgress();
        return output.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.FinishProgress();
        return output.WriteLineAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        output.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) this.FinishProgress();

        base.Dispose(disposing);
    }

    private void FinishProgress()
    {
        if (this.progressWidth == 0) return;

        output.WriteLine();
        this.progressWidth = 0;
    }
}
