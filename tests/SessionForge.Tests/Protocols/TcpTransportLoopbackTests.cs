using System.Net;
using System.Net.Sockets;
using System.Text;
using SessionForge.Core.Models;
using SessionForge.Protocols;
using SessionForge.Protocols.Telnet;

namespace SessionForge.Tests.Protocols;

/// <summary>Live loopback tests for the raw TCP and Telnet transports.</summary>
public sealed class TcpTransportLoopbackTests
{
    private static Session MakeSession(int port, SessionKind kind) => new() { Name = "t", Host = "127.0.0.1", Port = port, Kind = kind };

    [Fact]
    public async Task RawTcpRoundTrip()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                await s.WriteAsync("hello\r\n"u8.ToArray());
                var buf = new byte[4]; await s.ReadExactlyAsync(buf);
                await s.WriteAsync(buf);   // echo
                await Task.Delay(300);
            });
            await using var t = new TcpTerminalTransport(MakeSession(((IPEndPoint)listener.LocalEndpoint).Port, SessionKind.Raw), telnet: false);
            var received = new StringBuilder();
            var gotEcho = new TaskCompletionSource();
            t.DataReceived += d => { lock (received) { received.Append(Encoding.ASCII.GetString(d.Span)); if (received.ToString().Contains("ping")) gotEcho.TrySetResult(); } };
            await t.ConnectAsync(80, 24, CancellationToken.None);
            t.Write("ping"u8);
            await gotEcho.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("hello\r\n", received.ToString());
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task TelnetNegotiatesAndStripsCommands()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            byte[]? clientReply = null;
            var server = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                // IAC DO TERMINAL-TYPE, then visible text.
                await s.WriteAsync(new byte[] { 255, 253, 24 });
                await s.WriteAsync("login: "u8.ToArray());
                // The client opens with its own negotiation; collect what it sends for a moment.
                var all = new List<byte>(); var buf = new byte[256];
                using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                try { while (true) { int n = await s.ReadAsync(buf, window.Token); if (n == 0) break; all.AddRange(buf.AsSpan(0, n).ToArray()); } }
                catch (OperationCanceledException) { }
                clientReply = all.ToArray();
            });
            await using var t = new TcpTerminalTransport(MakeSession(((IPEndPoint)listener.LocalEndpoint).Port, SessionKind.Telnet), telnet: true);
            var text = new StringBuilder();
            var gotPrompt = new TaskCompletionSource();
            t.DataReceived += d => { lock (text) { text.Append(Encoding.ASCII.GetString(d.Span)); if (text.ToString().Contains("login: ")) gotPrompt.TrySetResult(); } };
            await t.ConnectAsync(80, 24, CancellationToken.None);
            await gotPrompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain('ÿ', text.ToString());
            await server;
            Assert.NotNull(clientReply);
            // IAC WILL TERMINAL-TYPE (255 251 24) must be present in the client's replies.
            bool found = false;
            for (int i = 0; i + 2 < clientReply!.Length; i++) if (clientReply[i] == 255 && clientReply[i + 1] == 251 && clientReply[i + 2] == 24) found = true;
            Assert.True(found, "client did not answer DO TERMINAL-TYPE with WILL");
        }
        finally { listener.Stop(); }
    }
}
