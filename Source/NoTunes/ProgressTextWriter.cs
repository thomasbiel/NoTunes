using System.Text;

namespace NoTunes;

internal sealed class ProgressTextWriter(TextWriter output, bool interactive) : TextWriter
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> activeProgress = new(StringComparer.Ordinal);
    private const string ProgressSeparator = " | ";
    private int progressWidth;

    public override Encoding Encoding => output.Encoding;

    public void WriteProgress(string message, bool complete)
    {
        lock (this.gate)
        {
            this.RenderProgress(message, complete);
        }
    }

    public void WriteProgress(string operation, string message, bool complete)
    {
        lock (this.gate)
        {
            if (!interactive)
            {
                output.WriteLine(message);
                return;
            }

            this.activeProgress[operation] = message;
            var combined = string.Join(ProgressSeparator, this.activeProgress.Values);
            if (complete) this.activeProgress.Remove(operation);

            this.RenderProgress(combined, this.activeProgress.Count == 0);
        }
    }

    private void RenderProgress(string message, bool complete)
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
        lock (this.gate)
        {
            this.FinishProgress();
            output.Write(value);
        }
    }

    public override void Write(string? value)
    {
        this.WriteDiagnostic(value, newLine: false);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (this.gate)
        {
            this.FinishProgress();
            output.Write(buffer);
        }
    }

    public override void WriteLine(string? value)
    {
        this.WriteDiagnostic(value, newLine: true);
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        lock (this.gate)
        {
            this.FinishProgress();
            output.WriteLine(buffer);
        }
    }

    public override Task WriteAsync(string? value)
    {
        this.Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        this.WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Write(buffer.Span);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.WriteLine(buffer.Span);
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        lock (this.gate)
        {
            output.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (this.gate)
            {
                this.FinishProgress();
            }
        }

        base.Dispose(disposing);
    }

    private void WriteDiagnostic(string? value, bool newLine)
    {
        lock (this.gate)
        {
            this.FinishProgress();
            output.Write(value);
            if (newLine) output.WriteLine();
        }
    }

    private void FinishProgress()
    {
        if (this.progressWidth == 0) return;

        output.WriteLine();
        this.progressWidth = 0;
    }
}
