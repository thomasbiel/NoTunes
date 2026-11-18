namespace NoTunes;

using CommandLine;
using Cli;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private const string DefaultUiCultureName = "en-US";
    private const string LongOptionPrefix = "--";
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        LongOptionPrefix + CliSyntax.Device,
        LongOptionPrefix + CliSyntax.Timeout,
        LongOptionPrefix + CliSyntax.StateFolder,
        LongOptionPrefix + CliSyntax.SourceFolder
    };

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
            var input = Console.IsInputRedirected ? null : Console.In;
            await using var services = new ServiceCollection()
                .AddNoTunes(output, error, input)
                .BuildServiceProvider(serviceProviderOptions);

            var context = services.GetRequiredService<CommandContext>();
            await using var parserOutput = new StringWriter(CultureInfo.InvariantCulture);
            using var parser = new Parser(settings => settings.HelpWriter = parserOutput);
            var result = parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions, InitOptions, RemoveCoverOptions>(args);
            if (result is Parsed<object>) ValidateOptionValues(args);

            return await result.MapResult<CommandOptions, Task<int>>(
                options => options.Execute(context, cancellation.Token),
                async errors =>
                {
                    var exitCode = errors.All(e => e is HelpRequestedError or HelpVerbRequestedError or VersionRequestedError)
                        ? ExitCodes.Success
                        : ExitCodes.InvalidArguments;

                    await (exitCode == ExitCodes.Success ? output : error).WriteAsync(parserOutput.ToString());
                    return exitCode;
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

    private static void ValidateOptionValues(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (StringComparer.Ordinal.Equals(argument, LongOptionPrefix)) return;

            var separator = argument.IndexOf('=');
            var option = separator < 0 ? argument : argument[..separator];
            if (!ValueOptions.Contains(option)) continue;

            if (separator >= 0)
            {
                if (string.IsNullOrWhiteSpace(argument[(separator + 1)..]))
                {
                    throw new InvalidArgumentsException($"{option} requires a value.");
                }

                continue;
            }

            if (index + 1 >= arguments.Count ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                arguments[index + 1].StartsWith(LongOptionPrefix, StringComparison.Ordinal))
            {
                throw new InvalidArgumentsException($"{option} requires a value.");
            }

            index++;
        }
    }
}
