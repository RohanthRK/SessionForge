using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SessionForge.Core.Models;
using SessionForge.Protocols.Automation;
using SessionForge.Protocols.Logging;
using SessionForge.Protocols.Sftp;
using SessionForge.Protocols.Ssh;
using SessionForge.Protocols.Telnet;
using SessionForge.Protocols.Vnc;
using SessionForge.Terminal;
using T = SessionForge.Protocols.Telnet.TelnetProtocol;

namespace SessionForge.Tests.Protocols;

public class ProtocolTests
{
    [Fact]
    public void TelnetStripsCommandsAndRepliesToNegotiation()
    {
        var t = new T("xterm");
        t.InitialNegotiation();
        var replies = new List<byte>();
        byte[] input = [(byte)'h', T.IAC, T.DO, T.OptNaws, (byte)'i', T.IAC, T.WILL, T.OptEcho, T.IAC, T.DO, 99, T.IAC, T.IAC];
        var data = t.Receive(input, replies);
        Assert.Equal(new byte[] { (byte)'h', (byte)'i', 255 }, data);
        Assert.True(t.ServerEchoes);
        // NAWS data sent (already offered, so no extra WILL), unknown option refused with WONT
        Assert.Equal(new byte[] { T.IAC, T.SB, T.OptNaws, 0, 80, 0, 24, T.IAC, T.SE, T.IAC, T.WONT, 99 }, replies);
    }

    [Fact]
    public void TelnetTerminalTypeSubnegotiation()
    {
        var t = new T("xterm-256color");
        t.InitialNegotiation();
        var replies = new List<byte>();
        t.Receive([T.IAC, T.SB, T.OptTType, 1, T.IAC, T.SE], replies);
        var expected = new List<byte> { T.IAC, T.SB, T.OptTType, 0 };
        expected.AddRange(Encoding.ASCII.GetBytes("XTERM-256COLOR"));
        expected.AddRange([T.IAC, T.SE]);
        Assert.Equal(expected, replies);
    }

    [Fact]
    public void TelnetSplitIacAcrossReads()
    {
        var t = new T("vt100");
        var replies = new List<byte>();
        Assert.Equal(new byte[] { (byte)'a' }, t.Receive([(byte)'a', T.IAC], replies));
        Assert.Equal(new byte[] { (byte)'b' }, t.Receive([T.WILL, T.OptSga, (byte)'b'], replies));
    }

    [Fact]
    public void TelnetEscapesOutgoing()
    {
        var t = new T("vt100");
        Assert.Equal(new byte[] { 1, 255, 255, (byte)'\r', 0 }, t.EscapeOutgoing([1, 255, (byte)'\r'], false));
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\n' }, t.EscapeOutgoing([(byte)'\r'], true));
    }

    [Fact]
    public void TelnetNawsEscapes255()
    {
        var t = new T("vt100");
        t.InitialNegotiation();
        var msg = t.SetWindowSize(255, 24);
        Assert.Equal(new byte[] { T.IAC, T.SB, T.OptNaws, 0, 255, 255, 0, 24, T.IAC, T.SE }, msg);
    }

    [Fact]
    public void PlainTextExtractorAppliesEditing()
    {
        var lines = new List<string>();
        var x = new PlainTextExtractor(lines.Add);
        x.Feed("\x1b[32mhello\x1b[0m world\r\n");
        x.Feed("progress 10%\rprogress 100%\r\n");
        x.Feed("abc\b\bX\r\n");
        x.Feed("typo\x1b[2D\x1b[Kne\r\n");
        Assert.Equal(["hello world", "progress 100%", "aXc", "tyne"], lines);
    }

    [Fact]
    public void SessionLoggerWritesPlainAndRaw()
    {
        var dir = Directory.CreateTempSubdirectory("sflog").FullName;
        try
        {
            var plainPath = Path.Combine(dir, "p.log");
            using (var l = new SessionLogger(plainPath, LogMode.PlainText, Encoding.UTF8))
            {
                l.Write(Encoding.UTF8.GetBytes("\x1b[1mbold\x1b[0m\r\nnext"));
            }
            var text = File.ReadAllText(plainPath);
            Assert.Contains("bold\r\nnext", text);
            Assert.False(text.Contains('\x1b'), "escape found in: " + text.Replace("\x1b", "<ESC>"));

            var rawPath = Path.Combine(dir, "r.log");
            var raw = Encoding.UTF8.GetBytes("\x1b[1mbold");
            using (var l = new SessionLogger(rawPath, LogMode.Raw, Encoding.UTF8)) l.Write(raw);
            Assert.Equal(raw, File.ReadAllBytes(rawPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LogPathPattern()
    {
        var p = SessionLogger.BuildPath(@"C:\logs", "{session}_{date:yyyy}.log", "web/01:prod");
        Assert.Equal(Path.Combine(@"C:\logs", $"web_01_prod_{DateTime.Now:yyyy}.log"), p);
    }

    [Fact]
    public void KeyNamesEncode()
    {
        var modes = new TerminalEmulator().Modes;
        Assert.Equal("\r", KeyNames.Encode("Enter", modes));
        Assert.Equal("\x03", KeyNames.Encode("Ctrl+C", modes));
        Assert.Equal("\x1b[A", KeyNames.Encode("Up", modes));
        Assert.Equal("\x1b[1;5A", KeyNames.Encode("ctrl+up", modes));
        Assert.Null(KeyNames.Encode("Hyper+X", modes));
    }

    private sealed class FakeTarget : IMacroTarget
    {
        public readonly StringBuilder Sent = new();
        public TerminalModes Modes { get; } = new TerminalEmulator().Modes;
        public event Action<string>? OutputText;
        public Action<string>? OnSend;
        public void SendText(string text) { lock (Sent) Sent.Append(text); OnSend?.Invoke(text); }
        public void Emit(string s) => OutputText?.Invoke(s);
    }

    [Fact]
    public async Task MacroRunnerWaitsForPromptAndSendsSecret()
    {
        var target = new FakeTarget();
        target.OnSend = s =>
        {
            if (s.StartsWith("ssh", StringComparison.Ordinal)) Task.Run(async () => { await Task.Delay(50); target.Emit("\x1b[1mPassword:\x1b[0m "); });
        };
        var macro = new Macro
        {
            Name = "login",
            Steps =
            [
                new() { Kind = MacroStepKind.SendText, Value = @"ssh box\r" },
                new() { Kind = MacroStepKind.WaitFor, Value = "[Pp]assword:", TimeoutMs = 3000 },
                new() { Kind = MacroStepKind.SendSecret, Value = "box" },
                new() { Kind = MacroStepKind.SendKey, Value = "Enter" },
            ],
        };
        var runner = new MacroRunner(target, _ => null, name => name == "box" ? "hunter2" : null);
        await runner.RunAsync(macro, CancellationToken.None);
        Assert.Equal("ssh box\rhunter2\r", target.Sent.ToString());
    }

    [Fact]
    public async Task MacroRunnerTimesOutAndDetectsRecursion()
    {
        var target = new FakeTarget();
        var wait = new Macro { Steps = [new() { Kind = MacroStepKind.WaitFor, Value = "never", TimeoutMs = 100 }] };
        var runner = new MacroRunner(target, _ => null, _ => null);
        await Assert.ThrowsAsync<MacroException>(() => runner.RunAsync(wait, CancellationToken.None));

        var loop = new Macro { Name = "loop", Steps = [new() { Kind = MacroStepKind.CallMacro, Value = "loop" }] };
        var r2 = new MacroRunner(target, n => n == "loop" ? loop : null, _ => null);
        await Assert.ThrowsAsync<MacroException>(() => r2.RunAsync(loop, CancellationToken.None));
    }

    [Fact]
    public void SshChainResolvesJumpSessionsAndDetectsLoops()
    {
        var bastion = new Session { Name = "bastion", Host = "b.example", Credential = new CredentialRef { Username = "jump" } };
        var inner = new Session { Name = "inner", Host = "10.0.0.5" };
        inner.Ssh.JumpHosts.Add(new JumpHost { SessionId = bastion.Id });
        inner.Ssh.JumpHosts.Add(new JumpHost { Host = "hop2", Port = 2222, Credential = new CredentialRef { Username = "u2" } });
        var all = new Dictionary<Guid, Session> { [bastion.Id] = bastion, [inner.Id] = inner };
        var chain = SshEndpoint.BuildChain(inner, id => all.GetValueOrDefault(id), false);
        Assert.Equal(["b.example", "hop2", "10.0.0.5"], chain.Select(c => c.Host));
        Assert.Equal(2222, chain[1].Port);
        Assert.Equal($"session:{inner.Id}:jump1", chain[1].SecretScope);

        bastion.Ssh.JumpHosts.Add(new JumpHost { SessionId = inner.Id });
        Assert.Throws<InvalidOperationException>(() => SshEndpoint.BuildChain(inner, id => all.GetValueOrDefault(id), false));
    }

    [Fact]
    public void RemotePathHelpers()
    {
        Assert.Equal("/", SftpService.Parent("/etc"));
        Assert.Equal("/etc", SftpService.Parent("/etc/ssh/"));
        Assert.Equal("/a/b", SftpService.Combine("/a", "b"));
        Assert.Equal("/b", SftpService.Combine("/", "b"));
    }

    [Fact]
    public void RemoteMonitorParsesLinuxProcOutput()
    {
        const string sample1 = """
            0.52 0.48 0.41 2/389 12345
            cpu  1000 0 500 8000 100 0 0 0 0 0
            MemTotal:        8000000 kB
            MemAvailable:    6000000 kB
            /dev/sda1        50G   20G   28G  42% /
            123456.78 400000.00
            3
            """;
        long[]? prev = null;
        var s1 = SessionForge.Protocols.Ssh.RemoteMonitor.Parse(sample1, ref prev);
        Assert.Null(s1.CpuPercent); // needs two samples
        Assert.Equal(0.52, s1.Load1);
        Assert.Equal(42, s1.DiskUsePercent);
        Assert.Equal("50G", s1.DiskSize);
        Assert.Equal(3, s1.Users);
        Assert.Equal(8000000, s1.MemTotalKb);
        Assert.Equal(1, (int)s1.Uptime!.Value.TotalDays);
        var sample2 = sample1.Replace("cpu  1000 0 500 8000 100", "cpu  1500 0 500 8400 100");
        var s2 = SessionForge.Protocols.Ssh.RemoteMonitor.Parse(sample2, ref prev);
        Assert.Equal(500.0 / 900 * 100, s2.CpuPercent!.Value, 3);
        Assert.Contains("CPU 56%", s2.Format());
    }

    [Fact]
    public void SshNetModeConversion()
    {
        Assert.Equal((short)755, SftpService.ToSshNetMode(0x1ED));
        Assert.Equal((short)644, SftpService.ToSshNetMode(0x1A4));
        Assert.Equal((short)0, SftpService.ToSshNetMode(0));
    }

    [Fact]
    public void SshWireRoundTrip()
    {
        var bytes = new SshWriter().String("ssh-ed25519").UInt32(7).MPInt([0x80, 1]).ToArray();
        var r = new SshReader(bytes);
        Assert.Equal("ssh-ed25519", r.String());
        Assert.Equal(7u, r.UInt32());
        Assert.Equal(new byte[] { 0, 0x80, 1 }, r.Bytes());
    }

    [Fact]
    public void VncAuthIsDesOfChallenge()
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var resp = RfbClient.VncAuthResponse("secret", challenge);
        Assert.Equal(16, resp.Length);
        Assert.NotEqual(challenge, resp);
        // Empty password is a DES weak key; must still work (BouncyCastle has no weak-key check).
        Assert.Equal(16, RfbClient.VncAuthResponse("", challenge).Length);
        // Deterministic and only first 8 chars matter
        Assert.Equal(RfbClient.VncAuthResponse("12345678", challenge), RfbClient.VncAuthResponse("123456789", challenge));
    }

    [Fact]
    public async Task RfbClientHandshakeAndDecodes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            async Task<byte[]> Read(int n) { var b = new byte[n]; await s.ReadExactlyAsync(b); return b; }
            await s.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            await Read(12);
            await s.WriteAsync(new byte[] { 1, 1 });          // one security type: None
            await Read(1);
            await s.WriteAsync(new byte[] { 0, 0, 0, 0 });    // SecurityResult OK
            await Read(1);                                     // ClientInit
            var init = new byte[2 + 2 + 16 + 4 + 4];
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(0), 4);
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), 2);
            BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), 4);
            Encoding.ASCII.GetBytes("test").CopyTo(init, 24);
            await s.WriteAsync(init);
            await Read(20);                                    // SetPixelFormat
            var encHdr = await Read(4);
            await Read(BinaryPrimitives.ReadUInt16BigEndian(encHdr.AsSpan(2)) * 4);
            await Read(10);                                    // FramebufferUpdateRequest

            // Update: raw 1x1 at (0,0) red, hextile 2x2 at (2,0) solid green background
            var m = new List<byte> { 0, 0, 0, 2 };
            void Rect(int x, int y, int w, int h, int enc)
            {
                var r = new byte[12];
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(0), (ushort)x);
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)y);
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), (ushort)w);
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), (ushort)h);
                BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(8), enc);
                m.AddRange(r);
            }
            Rect(0, 0, 1, 1, 0); m.AddRange(new byte[] { 0, 0, 255, 0 });          // BGRX: red
            Rect(2, 0, 2, 2, 5); m.Add(2); m.AddRange(new byte[] { 0, 255, 0, 0 }); // bg specified: green
            await s.WriteAsync(m.ToArray());
            await Read(10);                                    // incremental update request
        });

        var client = new RfbClient();
        var updated = new TaskCompletionSource();
        client.FramebufferUpdated += _ => updated.TrySetResult();
        await client.ConnectAsync("127.0.0.1", port, () => null, true, CancellationToken.None);
        Assert.Equal("test", client.DesktopName);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        client.WithFramebuffer((fb, w, h) =>
        {
            Assert.Equal((4, 2), (w, h));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, fb[0..4]);           // red, alpha forced opaque
            int o = (1 * w + 3) * 4; // pixel (3,1)
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, fb[o..(o + 4)]); // green
        });
        await client.DisposeAsync();
        listener.Stop();
    }
}
