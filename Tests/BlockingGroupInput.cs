namespace NoTunes.Tests;

internal sealed class BlockingGroupInput : TextReader
{
    private readonly TaskCompletionSource<string?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override string? ReadLine()
    {
        this.Started.TrySetResult();
        return this.response.Task.GetAwaiter().GetResult();
    }

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(this.ReadLine());
    }

    public void Release()
    {
        this.response.TrySetResult(null);
    }
}
