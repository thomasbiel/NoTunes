namespace NoTunes;

using CommandLine;
using NoTunes.Cli;
using System.Globalization;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await Parser.Default
                .ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions>(args)
                .MapResult(
                    (DevicesOptions options) => Commands.DevicesAsync(options, cancellation.Token),
                    (ProbeOptions options) => Commands.ProbeAsync(options, cancellation.Token),
                    (ScanOptions options) => Commands.ScanAsync(options, cancellation.Token),
                    (SyncOptions options) => Commands.SyncAsync(options, cancellation.Token),
                    errors => Task.FromResult(errors.All(e => e is HelpRequestedError
                        or HelpVerbRequestedError or VersionRequestedError) ? 0 : 2));
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException
            or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
