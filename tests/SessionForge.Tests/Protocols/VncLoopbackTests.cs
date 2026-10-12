using System.Net;
using System.Net.Sockets;
using System.Text;
using SessionForge.Protocols.Vnc;

namespace SessionForge.Tests.Protocols;

/// <summary>Runs RfbClient against an in-process RFB 3.8 server that serves a 2x2 Raw-encoded framebuffer.</summary>
public sealed class VncLoopbackTests
{
    private static async Task ServeAsync(TcpListener listener, byte[] pixelsBgrx, string? password, Action<string> record)
    {
        using var c = await listener.AcceptTcpClientAsync();
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        var clientVer = new byte[12]; await s.ReadExactlyAsync(clientVer);
        record(Encoding.ASCII.GetString(clientVer));

        if (password == null)
        {
            await s.WriteAsync(new byte[] { 1, 1 });          // one type: None
            var chosen = new byte[1]; await s.ReadExactlyAsync(chosen);
        }
        else
        {
            await s.WriteAsync(new byte[] { 1, 2 });          // VNC Authentication
            var chosen = new byte[1]; await s.ReadExactlyAsync(chosen);
            var challenge = new byte[16]; Random.Shared.NextBytes(challenge);
            await s.WriteAsync(challenge);
            var resp = new byte[16]; await s.ReadExactlyAsync(resp);
            bool ok = resp.AsSpan().SequenceEqual(RfbClient.VncAuthResponse(password, challenge));
            if (!ok)
            {
                var reason = Encoding.ASCII.GetBytes("bad password");
                await s.WriteAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0, (byte)reason.Length });
                await s.WriteAsync(reason);
                return;
            }
        }
        await s.WriteAsync(new byte[4]);                       // SecurityResult OK
        var shared = new byte[1]; await s.ReadExactlyAsync(shared);

        var init = new List<byte> { 0, 2, 0, 2 };              // 2x2
        init.AddRange(new byte[16]);                           // pixel format (client overrides)
        var name = Encoding.UTF8.GetBytes("loopback");
        init.AddRange(new byte[] { 0, 0, 0, (byte)name.Length });
        init.AddRange(name);
        await s.WriteAsync(init.ToArray());

        // Read client messages until the first FramebufferUpdateRequest (type 3), then answer with one Raw rect.
        while (true)
        {
            var t = new byte[1]; await s.ReadExactlyAsync(t);
            switch (t[0])
            {
                case 0: await s.ReadExactlyAsync(new byte[19]); break;                       // SetPixelFormat
                case 2: { var h = new byte[3]; await s.ReadExactlyAsync(h); int n = (h[1] << 8) | h[2]; await s.ReadExactlyAsync(new byte[n * 4]); break; }
                case 3:
                {
                    await s.ReadExactlyAsync(new byte[9]);
                    var upd = new List<byte> { 0, 0, 0, 1, 0, 0, 0, 0, 0, 2, 0, 2, 0, 0, 0, 0 };   // 1 rect 0,0 2x2 Raw
                    upd.AddRange(pixelsBgrx);
                    await s.WriteAsync(upd.ToArray());
                    await Task.Delay(1500);                                                   // keep open while client reads
                    return;
                }
                default: return;
            }
        }
    }

    [Fact]
    public async Task ConnectsWithoutAuthAndDecodesRawRectangle()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            // Client pixel format is 32bpp little-endian BGRX; values below are red, green, blue, white.
            byte[] px = [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
            string? seen = null;
            var server = ServeAsync(listener, px, null, v => seen = v);

            await using var client = new RfbClient();
            var updated = new TaskCompletionSource();
            client.FramebufferUpdated += _ => updated.TrySetResult();
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, () => null, true, CancellationToken.None);
            Assert.Equal(2, client.Width);
            Assert.Equal(2, client.Height);
            Assert.Equal("loopback", client.DesktopName);
            Assert.Equal("RFB 003.008\n", seen);

            await updated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            byte[] fb = [];
            client.WithFramebuffer((buf, _, _) => fb = buf.Take(16).ToArray());
            // Whatever channel order the client uses internally, the four pixels must be distinct and white must be all-0xFF.
            Assert.Equal(4, Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(fb, i * 4) & 0xFFFFFF).Distinct().Count());
            Assert.Equal(0xFFFFFFu, BitConverter.ToUInt32(fb, 12) & 0xFFFFFF);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task VncPasswordAcceptedAndRejected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var good = ServeAsync(listener, new byte[16], "hunter2", _ => { });
            await using (var ok = new RfbClient())
                await ok.ConnectAsync("127.0.0.1", port, () => "hunter2", false, CancellationToken.None);
            await good;

            var bad = ServeAsync(listener, new byte[16], "hunter2", _ => { });
            await using var wrong = new RfbClient();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.ConnectAsync("127.0.0.1", port, () => "nope", false, CancellationToken.None));
            await bad;
        }
        finally { listener.Stop(); }
    }
}
