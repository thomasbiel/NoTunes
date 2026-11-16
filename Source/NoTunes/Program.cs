namespace NoTunes;

using CommandLine;
using NoTunes.Cli;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private const string DefaultUiCultureName = "en-US";

    public static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, Console.Out, Console.Error);
    }

    private static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(DefaultUiCultureName);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            var serviceProviderOptions = new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true };
            await using var services = new ServiceCollection().AddNoTunes(output, error).BuildServiceProvider(serviceProviderOptions);
            var commands = services.GetRequiredService<Commands>();
            using var parser = new Parser(settings => settings.HelpWriter = output);
            return await parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions>(args).MapResult(
                (DevicesOptions options) => commands.DevicesAsync(options, cancellation.Token),
                (ProbeOptions options) => commands.ProbeAsync(options, cancellation.Token),
                (ScanOptions options) => commands.ScanAsync(options, cancellation.Token),
                (SyncOptions options) => commands.SyncAsync(options, cancellation.Token),
                errors =>
                {
                    return Task.FromResult(errors.All(e => e is HelpRequestedError or HelpVerbRequestedError or VersionRequestedError)
                        ? ExitCodes.Success
                        : ExitCodes.InvalidArguments);
                });
        }
        catch (InvalidArgumentsException e)
        {
            await error.WriteLineAsync($"Error: {e.Message}");
            return ExitCodes.InvalidArguments;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception e)
        {
            await error.WriteLineAsync($"Error: {e.Message}");
            return ExitCodes.Error;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
