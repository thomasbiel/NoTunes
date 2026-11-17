namespace NoTunes.Cli;

internal sealed class GroupResolutionPrompt(TextReader input, TextWriter error)
{
    public async ValueTask<string?> ReadAsync(string description, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ReferenceEquals(input, TextReader.Null)) return null;

        await error.WriteLineAsync(description.AsMemory(), cancellationToken);
        await error.WriteAsync("Target name (empty to cancel): ".AsMemory(), cancellationToken);
        await error.FlushAsync(cancellationToken);
        return await Task.Run(input.ReadLine, cancellationToken).WaitAsync(cancellationToken);
    }
}
