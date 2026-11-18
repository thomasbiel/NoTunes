using System.Diagnostics;
using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CliProcessTests
{
    [Test]
    public async Task Reports_Scan_Progress_On_Standard_Error_And_Preserves_Json_Output()
    {
        using var source = new Music.SyncWorkspace();
        await File.WriteAllTextAsync(Path.Combine(source.Root, "song.mp3"), "abc");
        var result = await RunAsync("scan", source.Root, "--json");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
        Assert.That(result.Error, Does.Contain("Scanning and hashing source: 0% (0/1)"));
        Assert.That(result.Error, Does.Contain("Scanning and hashing source: 100% (1/1)"));
        using var document = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.That(document.RootElement.GetProperty("Tracks").GetArrayLength(), Is.EqualTo(1));
    }

    [Test]
    public async Task Removes_A_Local_Cover_Through_The_Cli([Values] bool json)
    {
        using var source = new Music.SyncWorkspace();
        var path = SourceTestData.WriteTrack(source.Root, "song.mp3",
            file => file.Tag.Pictures = [FeatureTestData.FrontCover([1, 2, 3, 4])]);

        var result = await RunAsync(json ? ["remove-cover", path, "--json"] : ["remove-cover", path]);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
        Assert.That(result.Error, Is.Empty);
        if (json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(result.Output);
            Assert.That(document.RootElement.GetProperty("Path").GetString(), Is.EqualTo(path));
            Assert.That(document.RootElement.GetProperty("RemovedCovers").GetInt32(), Is.EqualTo(1));
        }
        else
        {
            Assert.That(result.Output, Does.Contain("Removed 1 embedded cover(s)"));
        }

        using var verified = TagLib.File.Create(path);
        Assert.That(verified.Tag.Pictures, Is.Empty);
    }

    [Test]
    public async Task Resolves_A_Cover_Command_Path_Relative_To_The_Selected_Music_Folder()
    {
        using var source = new Music.SyncWorkspace();
        var path = SourceTestData.WriteTrack(source.Root, "song.mp3",
            file => file.Tag.Pictures = [FeatureTestData.FrontCover()]);

        var result = await RunAsync(
            "remove-cover",
            "song.mp3",
            "--folder",
            source.Root);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
        using var verified = TagLib.File.Create(path);
        Assert.That(verified.Tag.Pictures, Is.Empty);
    }

    [Test]
    public async Task Requires_A_File_For_Cover_Removal()
    {
        var result = await RunAsync("remove-cover");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.InvalidArguments));
        Assert.That(result.Output, Is.Empty);
        Assert.That(result.Error, Is.Not.Empty);
    }

    [Test]
    public async Task Executes_A_Valid_Scan_Through_The_Shared_MapResult_Callback()
    {
        using var source = new Music.SyncWorkspace();
        var result = await RunAsync("scan", source.Root, "--json");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
        Assert.That(result.Error, Does.Contain("Scanning and hashing source: 100% (0/0)"));
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.That(json.RootElement.GetProperty("Tracks").GetArrayLength(), Is.Zero);
    }

    [TestCase("scan")]
    [TestCase("sync")]
    [TestCase("remove-cover")]
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
    [TestCase("remove-cover")]
    public async Task Help_Still_Succeeds(string command)
    {
        var result = await RunAsync(command, "--help");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
        Assert.That(result.Output, Does.Contain("--help"));
        Assert.That(result.Error, Is.Empty);
    }

    [TestCase("scan", "--unknown")]
    [TestCase("sync", "--unknown")]
    [TestCase("devices", "--unknown")]
    [TestCase("probe", "--unknown")]
    [TestCase("sync", "--timeout=invalid")]
    [TestCase("probe", "--timeout=invalid")]
    public async Task Parser_Errors_Use_Standard_Error_Without_Contaminating_Json(string command, string option)
    {
        var result = await RunAsync(command, "--json", option);
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.InvalidArguments), result.Error);
        Assert.That(result.Error, Is.Not.Empty);
        Assert.That(result.Output, Is.Empty);
    }

    [Test]
    public async Task Version_Uses_Standard_Output()
    {
        var result = await RunAsync("--version");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Success), result.Error);
        Assert.That(result.Output, Is.Not.Empty);
        Assert.That(result.Error, Is.Empty);
    }

    [TestCase("--timeout", false)]
    [TestCase("--timeout", true)]
    [TestCase("--device", false)]
    [TestCase("--device", true)]
    [TestCase("--state-folder", false)]
    [TestCase("--state-folder", true)]
    [TestCase("--device=", false)]
    [TestCase("--state-folder=", false)]
    [TestCase("--timeout=", false)]
    public async Task Rejects_Options_Without_A_Value_Before_Scanning(string option, bool beforeJson)
    {
        var missing = Path.Combine(Path.GetTempPath(), "NoTunes-missing-" + Guid.NewGuid().ToString("N"));
        string[] arguments = beforeJson ? ["sync", missing, option, "--json"] : ["sync", missing, "--json", option];
        var result = await RunAsync(arguments);
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.InvalidArguments), result.Error);
        Assert.That(result.Error, Does.Contain(option.TrimEnd('=')));
        Assert.That(result.Error, Does.Not.Contain("Scanning"));
        Assert.That(result.Output, Is.Empty);
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
