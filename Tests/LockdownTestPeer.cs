using System.Net;
using System.Net.Sockets;
using NoTunes.Apple;
using NUnit.Framework;
using Org.BouncyCastle.Tls;

namespace NoTunes.Tests;

internal static class LockdownTestPeer
{
    private static readonly Lazy<TlsTestCredentials> Keys = new(() => new TlsTestCredentials());

    public static async Task RunAsync(
        Func<Dictionary<string, object>, Dictionary<string, object>> reply,
        Func<LockdownClient, Task> client,
        bool network = false)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new TcpClient();
        var record = Keys.Value.Record();
        record["HostID"] = "test-host";
        record["SystemBUID"] = "test-buid";
        var transport = new ReplyUsbMuxTransport(new ScriptedStream(
            ProtocolFrames.UsbMux(1, Plist.Dict(("PairRecordData", Plist.ToXml(record))))));

        var serve = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var onCancel = deadline.Token.Register(peer.Dispose);
            var raw = peer.GetStream();
            await using var plaintext = new PlistConnection(raw, TimeSpan.FromSeconds(5));
            var start = await plaintext.ReceiveAsync(deadline.Token);
            Assert.That(start.Text("Request"), Is.EqualTo("StartSession"));
            Assert.That(start.Text("HostID"), Is.EqualTo("test-host"));
            await plaintext.SendAsync(Plist.Dict(
                ("Request", "StartSession"), ("SessionID", "test-session"), ("EnableSessionSSL", true)), deadline.Token);

            var protocol = new TlsServerProtocol(raw);
            try
            {
                protocol.Accept(new PairedTlsServer(Keys.Value));
                await using var encrypted = new PlistConnection(protocol.Stream, TimeSpan.FromSeconds(5));
                while (true)
                {
                    var request = await encrypted.ReceiveAsync(deadline.Token);
                    var response = reply(request);
                    response["Request"] = request.Text("Request");
                    await encrypted.SendAsync(response, deadline.Token);
                }
            }
            catch (Exception error) when (deadline.IsCancellationRequested && error is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (EndOfStreamException)
            {
            }
            finally
            {
                protocol.Close();
            }
        }, deadline.Token);

        try
        {
            await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
            await using var lockdown = new LockdownClient(
                new UsbMuxClient(transport, TimeSpan.FromSeconds(5)),
                new UsbDevice(1, "test-device", 1) { NetworkAddress = network ? "127.0.0.1" : null },
                TimeSpan.FromSeconds(5),
                socket.GetStream());

            await lockdown.StartPairedSessionAsync(deadline.Token);
            await client(lockdown).WaitAsync(deadline.Token);
        }
        finally
        {
            await deadline.CancelAsync();
            socket.Dispose();
            await serve;
        }
    }
}
