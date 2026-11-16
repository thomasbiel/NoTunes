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
            using var services = new ServiceCollection().AddNoTunes().BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var commands = services.GetRequiredService<Commands>();
            return await Parser.Default
                .ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions>(args)
                .MapResult(
                    (DevicesOptions options) => commands.DevicesAsync(options, cancellation.Token),
                    (ProbeOptions options) => commands.ProbeAsync(options, cancellation.Token),
                    (ScanOptions options) => commands.ScanAsync(options, cancellation.Token),
                    (SyncOptions options) => commands.SyncAsync(options, cancellation.Token),
                    errors => Task.FromResult(errors.All(e => e is HelpRequestedError
                        or HelpVerbRequestedError or VersionRequestedError) ? ExitCodes.Success : ExitCodes.InvalidArguments));
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException
            or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return ExitCodes.Error;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
