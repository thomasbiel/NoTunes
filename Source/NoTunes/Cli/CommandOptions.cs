using System.Text.Json;

namespace NoTunes.Cli;

public abstract class CommandOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public abstract Task<int> Execute(CommandContext context, CancellationToken token);

    protected static void ValidateAdditionalArguments(IEnumerable<string> arguments)
    {
        // The parser silently ignores surplus positional values unless a verb captures them with [Value].
        var additional = arguments.ToArray();
        if (additional.Length != 0)
        {
            throw new InvalidArgumentsException($"Unexpected positional argument(s): {string.Join(", ", additional)}");
        }
    }

    protected static void WriteJson<T>(TextWriter output, T value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }
}
