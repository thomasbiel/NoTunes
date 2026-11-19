using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class OperationProgressTests
{
    [Test]
    public void Reports_Each_Percentage_Once_For_Large_Operations()
    {
        const int total = 1000;
        using var output = new StringWriter();
        var progress = new OperationProgress(output, "Work", total);
        for (var index = 0; index < total; index++)
        {
            progress.Advance();
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(101));
        Assert.That(lines[0], Is.EqualTo("Work: 0% (0/1,000)"));
        Assert.That(lines[50], Is.EqualTo("Work: 50% (500/1,000)"));
        Assert.That(lines[^1], Is.EqualTo("Work: 100% (1,000/1,000)"));
    }

    [Test]
    public void Displays_Human_Readable_Byte_Progress_In_Interactive_And_Redirected_Output([Values] bool interactive)
    {
        using var output = new StringWriter();
        using var writer = new ProgressTextWriter(output, interactive);
        var progress = new OperationProgress(
            writer,
            "Transferring assets",
            45_600_000,
            ProgressUnit.Bytes);

        progress.Report(12_300_000);
        progress.Report(45_600_000);

        Assert.That(output.ToString(), Does.Contain("Transferring assets: 26% (12.3 MB / 45.6 MB)"));
        Assert.That(output.ToString(), Does.Contain("Transferring assets: 100% (45.6 MB / 45.6 MB)"));
    }

    [Test]
    public void Handles_Empty_Operations_And_Does_Not_Repeat_Completion()
    {
        using var output = new StringWriter();
        var progress = new OperationProgress(output, "Empty", 0);
        progress.Report(0);
        Assert.That(output.ToString(), Is.EqualTo($"Empty: 100% (0/0){Environment.NewLine}"));
    }

    [Test]
    public void Keeps_Progress_Monotonic_Without_Overflowing_Large_Byte_Counts()
    {
        using var output = new StringWriter();
        var progress = new OperationProgress(output, "Bytes", long.MaxValue);
        progress.Report(long.MaxValue / 2);
        progress.Report(0);
        progress.Report(long.MaxValue);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines[1], Does.Contain("49%"));
        Assert.That(lines[^1], Does.Contain("100%"));
    }

    [TestCase(-1)]
    [TestCase(11)]
    public void Rejects_Progress_Outside_The_Known_Total(long completed)
    {
        var progress = new OperationProgress(TextWriter.Null, "Work", 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Report(completed));
    }
}
