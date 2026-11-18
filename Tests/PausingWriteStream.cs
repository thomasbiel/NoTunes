namespace NoTunes.Tests;

internal sealed class PausingWriteStream : NonSeekableTestStream
{
    private readonly MemoryStream written = new();
    private int writes;

    public TaskCompletionSource FirstWriteStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ResumeFirstWrite { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public byte[] Written => this.written.ToArray();

    public override bool CanRead => false;

    public override bool CanWrite => true;







    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref this.writes) == 1)
        {
            var half = buffer.Length / 2;
            this.written.Write(buffer.Span[..half]);
            this.FirstWriteStarted.TrySetResult();
            await this.ResumeFirstWrite.Task.WaitAsync(cancellationToken);
            buffer = buffer[half..];
        }

        this.written.Write(buffer.Span);
    }



    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();





    protected override void Dispose(bool disposing)
    {
        if (disposing) this.written.Dispose();

        base.Dispose(disposing);
    }
}
