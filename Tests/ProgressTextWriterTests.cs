using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ProgressTextWriterTests
{
    [Test]
    public void Replaces_The_Current_Line_And_Only_Ends_It_On_Completion()
    {
        using var output = new StringWriter();
        using var writer = new ProgressTextWriter(output, interactive: true);
        var progress = new OperationProgress(writer, "Work", 2);
        progress.Advance();
        Assert.That(output.ToString(), Is.EqualTo("\rWork: 0% (0/2)\rWork: 50% (1/2)"));
        progress.Advance();
        Assert.That(output.ToString(), Does.EndWith($"\rWork: 100% (2/2){Environment.NewLine}"));
        Assert.That(output.ToString().Split(Environment.NewLine), Has.Length.EqualTo(2));
    }

    [Test]
    public void Clears_Characters_Left_By_A_Previous_Longer_Message()
    {
        using var output = new StringWriter();
        using var writer = new ProgressTextWriter(output, interactive: true);
        writer.WriteProgress("Long progress", complete: false);
        writer.WriteProgress("Short", complete: true);
        Assert.That(output.ToString(), Is.EqualTo($"\rLong progress\rShort        {Environment.NewLine}"));
    }

    [Test]
    public async Task Keeps_Diagnostics_And_Resumed_Progress_On_Separate_Lines([Values] bool asynchronous)
    {
        using var output = new StringWriter();
        using var writer = new ProgressTextWriter(output, interactive: true);
        writer.WriteProgress("Working", complete: false);
        if (asynchronous)
        {
            await writer.WriteLineAsync("Skipped file".AsMemory(), CancellationToken.None);
        }
        else
        {
            writer.WriteLine("Skipped file");
        }

        writer.WriteProgress("Finished", complete: true);
        Assert.That(output.ToString(), Is.EqualTo(
            $"\rWorking{Environment.NewLine}Skipped file{Environment.NewLine}\rFinished{Environment.NewLine}"));
    }

    [Test]
    public void Preserves_Readable_Log_Lines_When_Output_Is_Redirected()
    {
        using var output = new StringWriter();
        using var writer = new ProgressTextWriter(output, interactive: false);
        var progress = new OperationProgress(writer, "Work", 2);
        progress.Advance();
        progress.Advance();
        Assert.That(output.ToString(), Is.EqualTo(
            $"Work: 0% (0/2){Environment.NewLine}"
            + $"Work: 50% (1/2){Environment.NewLine}Work: 100% (2/2){Environment.NewLine}"));
    }

    [Test]
    public void Finishes_An_Interrupted_Line_Without_Closing_The_Injected_Writer()
    {
        using var output = new StringWriter();
        using (var writer = new ProgressTextWriter(output, interactive: true))
        {
            writer.WriteProgress("Working", complete: false);
        }

        output.Write("Still open");
        Assert.That(output.ToString(), Is.EqualTo($"\rWorking{Environment.NewLine}Still open"));
    }
}
