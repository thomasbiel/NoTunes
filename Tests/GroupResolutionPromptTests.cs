using Microsoft.Extensions.DependencyInjection;
using NoTunes.Cli;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class GroupResolutionPromptTests
{
    [Test]
    public async Task Cancels_While_The_Console_Reader_Is_Waiting_For_An_Answer()
    {
        using var input = new BlockingGroupInput();
        using var cancellation = new CancellationTokenSource();
        var prompt = new GroupResolutionPrompt(input, TextWriter.Null);
        var read = Task.Run(() => prompt.ReadAsync("Ambiguous artist", cancellation.Token).AsTask());
        try
        {
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancellation.CancelAsync();
            await Assert.CatchAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            input.Release();
        }
    }

    [Test]
    public async Task Reads_The_Injected_Input_And_Keeps_Prompts_Out_Of_Json_Output()
    {
        using var input = new StringReader("Chosen Artist\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var services = new ServiceCollection().AddNoTunes(output, error, input).BuildServiceProvider();
        var prompt = services.GetRequiredService<SyncEngine>().PromptForGroupName!;
        Assert.That(await prompt("Ambiguous artist in one.mp3", CancellationToken.None),
            Is.EqualTo("Chosen Artist"));
        Assert.That(output.ToString(), Is.Empty);
        Assert.That(error.ToString(), Does.Contain("one.mp3").And.Contain("Target name"));
    }

    [Test]
    public async Task Returns_No_Answer_When_Interactive_Input_Is_Unavailable()
    {
        using var error = new StringWriter();
        var prompt = new GroupResolutionPrompt(TextReader.Null, error);
        Assert.That(await prompt.ReadAsync("Ambiguous artist", CancellationToken.None), Is.Null);
        Assert.That(error.ToString(), Is.Empty);
    }
}
