using NoTunes.Music;

namespace NoTunes.Apple;

internal sealed class WirelessSession : IAsyncDisposable
{
    private const int AssertionSeconds = 60;
    private const string LeaseFileName = "wifi.lock";
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<string, CancellationToken, Task<Stream>> openService;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource lifetime;
    private readonly List<Task> workers = [];
    private PlistConnection? heartbeat;
    private PlistConnection? assertion;
    private Exception? failure;

    internal WirelessSession(
        Func<string, CancellationToken, Task<Stream>> openService,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        this.openService = openService;
        this.timeout = timeout;
        this.lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal CancellationToken Token => this.lifetime.Token;

    // One heartbeat owns all service connections for this host. Keep it across batches and recovery reconnects.
    public static async Task<T> RunAsync<T>(
        UsbMuxClient mux,
        UsbDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> operation)
    {
        if (device.NetworkAddress is null) return await operation(cancellationToken);

        using var lease = AcquireLease(device.Udid);
        await using var lockdown = await LockdownClient.ConnectAsync(
            mux,
            device,
            timeout,
            cancellationToken);

        await lockdown.StartPairedSessionAsync(cancellationToken);
        await lockdown.GetInfoAsync(cancellationToken);
        await using var session = new WirelessSession(lockdown.OpenServiceAsync, timeout, cancellationToken);
        try
        {
            await session.StartAsync(RenewalInterval, SettlingTime);
            var result = await operation(session.Token);
            session.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && session.failure is not null)
        {
            throw new IOException("The iPhone Wi-Fi keep-alive failed. Unlock the phone and repeat the command.", session.failure);
        }
    }

    internal static FileStream AcquireLease(string udid)
    {
        var directory = DeviceState.DirectoryFor(DeviceState.Root(null), udid);
        try
        {
            return new FileStream(
                Path.Combine(directory, LeaseFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("Another NoTunes Wi-Fi command is using this iPhone. Wait for it to finish.", error);
        }
    }

    internal async Task StartAsync(TimeSpan renewalInterval, TimeSpan settlingTime)
    {
        this.heartbeat = new PlistConnection(
            await this.openService(DeviceServices.Heartbeat, this.Token), HeartbeatTimeout);

        this.workers.Add(this.WatchAsync(this.AnswerHeartbeatAsync));
        await this.RenewAssertionAsync(this.Token);
        this.workers.Add(this.WatchAsync(token => this.RefreshAssertionsAsync(renewalInterval, token)));
        await Task.Delay(settlingTime, this.Token);
        this.Token.ThrowIfCancellationRequested();
    }

    private async Task WatchAsync(Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(this.Token);
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref this.failure, error, null);
            await this.lifetime.CancelAsync();
        }
    }

    private async Task AnswerHeartbeatAsync(CancellationToken token)
    {
        while (true)
        {
            var message = await this.heartbeat!.ReceiveAsync(token);
            var command = message.Text(WirelessProtocol.Command);
            if (command == WirelessProtocol.SleepyTime) throw new IOException("The iPhone is going to sleep.");
            if (command != WirelessProtocol.Marco) throw new InvalidDataException("Unexpected Wi-Fi heartbeat command.");

            await this.heartbeat.SendAsync(Plist.Dict((WirelessProtocol.Command, WirelessProtocol.Polo)), token);
        }
    }

    private async Task RefreshAssertionsAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(token))
        {
            await this.RenewAssertionAsync(token);
        }
    }

    private async Task RenewAssertionAsync(CancellationToken token)
    {
        var next = new PlistConnection(await this.openService(DeviceServices.AssertionAgent, token), this.timeout);
        try
        {
            var reply = await next.ExchangeAsync(Plist.Dict(
                (WirelessProtocol.AssertionCommand, WirelessProtocol.CreateAssertion),
                (WirelessProtocol.AssertionType, WirelessProtocol.WirelessSyncAssertion),
                (WirelessProtocol.AssertionName, AppleClient.Name),
                (WirelessProtocol.AssertionTimeout, (long)AssertionSeconds)), token);

            if (reply.Count != 0) throw new InvalidDataException("The iPhone rejected the wireless power assertion.");
        }
        catch
        {
            await next.DisposeAsync();
            throw;
        }

        var previous = this.assertion;
        this.assertion = next;
        if (previous is not null) await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();
        await Task.WhenAll(this.workers);
        try
        {
            if (this.assertion is not null) await this.assertion.DisposeAsync();
        }
        finally
        {
            if (this.heartbeat is not null) await this.heartbeat.DisposeAsync();
            this.lifetime.Dispose();
        }
    }
}
