using System.Diagnostics;
using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class CliProcessTests
{
    [TestCase("scan")]
    [TestCase("sync")]
    public async Task Rejects_Additional_Source_Arguments_Before_Scanning(string command)
    {
        var missing = Path.Combine(Path.GetTempPath(), "NoTunes-missing-" + Guid.NewGuid().ToString("N"));
        var result = await RunAsync(command, missing, "unexpected");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.InvalidArguments), result.Error);
        Assert.That(result.Error, Does.Contain("unexpected"));
        Assert.That(result.Output, Is.Empty);
    }

    [TestCase("devices")]
    [TestCase("probe")]
    public async Task Rejects_Positional_Arguments_For_Device_Commands(string command)
    {
        var result = await RunAsync(
            command,
            "unexpected",
            "--timeout",
            "0");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.InvalidArguments), result.Error);
        Assert.That(result.Error, Does.Contain("unexpected"));
        Assert.That(result.Output, Is.Empty);
    }

    [TestCase("devices", "0")]
    [TestCase("devices", "301")]
    [TestCase("probe", "-1")]
    [TestCase("probe", "301")]
    [TestCase("sync", "0")]
    [TestCase("sync", "301")]
    public async Task Invalid_Timeout_Uses_The_Argument_Error_Exit_Code(string command, string timeout)
    {
        var result = await RunAsync(command, "--timeout", timeout);
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.InvalidArguments), result.Error);
        Assert.That(result.Error, Does.Contain("--timeout"));
        Assert.That(result.Output, Is.Empty);
    }

    [TestCase("scan")]
    [TestCase("sync")]
    [TestCase("devices")]
    [TestCase("probe")]
    public async Task Help_Still_Succeeds(string command)
    {
        var result = await RunAsync(command, "--help");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(ApplicationServices).Assembly.Location);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
