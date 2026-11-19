namespace NoTunes;

internal sealed class OperationProgress
{
    private const int MaximumPercentage = 100;
    private readonly TextWriter output;
    private readonly string operation;
    private readonly long total;
    private readonly ProgressUnit unit;
    private int lastPercentage = -1;
    private long completed;

    public OperationProgress(
        TextWriter output,
        string operation,
        long total,
        ProgressUnit unit = ProgressUnit.Items)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        this.output = output;
        this.operation = operation;
        this.total = total;
        this.unit = unit;
        this.Report(0);
    }

    public void Advance()
    {
        this.Report(this.completed + 1);
    }

    public void Report(long completed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(completed, this.total);

        if (completed < this.completed) return;

        this.completed = completed;
        var percentage = this.total == 0
            ? MaximumPercentage
            : (int)((decimal)completed * MaximumPercentage / this.total);

        if (percentage <= this.lastPercentage) return;

        this.lastPercentage = percentage;
        var quantities = this.unit == ProgressUnit.Bytes
            ? $"{DisplayFormat.Bytes(completed)} / {DisplayFormat.Bytes(this.total)}"
            : $"{DisplayFormat.Number(completed)}/{DisplayFormat.Number(this.total)}";

        var message = $"{this.operation}: {percentage}% ({quantities})";
        if (this.output is ProgressTextWriter progressOutput)
        {
            progressOutput.WriteProgress(this.operation, message, percentage == MaximumPercentage);
        }
        else
        {
            this.output.WriteLine(message);
        }
    }
}
