using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SessionForge.Core.Models;
using SessionForge.Core.Security;
using SessionForge.Protocols;
using SessionForge.Protocols.Sftp;
using SessionForge.Protocols.Ssh;

namespace SessionForge.Tests.Integration;

/// <summary>
/// End-to-end tests against a real OpenSSH server. Configure with environment variables:
/// SF_SSH_HOST, SF_SSH_PORT, SF_SSH_USER, SF_SSH_KEY (private key path, unencrypted),
/// SF_SSH_AUTHKEYS (authorized_keys path, writable — used to add a temporary encrypted test key).
/// Tests are reported as skipped when the server isn't configured.
/// </summary>
public sealed class SshIntegrationTests : IDisposable
{
    private readonly string? _host = Environment.GetEnvironmentVariable("SF_SSH_HOST");
    private readonly int _port = int.TryParse(Environment.GetEnvironmentVariable("SF_SSH_PORT"), out var p) ? p : 22;
    private readonly string? _user = Environment.GetEnvironmentVariable("SF_SSH_USER");
    private readonly string? _key = Environment.GetEnvironmentVariable("SF_SSH_KEY");
    private readonly string? _authKeys = Environment.GetEnvironmentVariable("SF_SSH_AUTHKEYS");
    private readonly string _dir = Directory.CreateTempSubdirectory("sfint").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void RequireServer() => Skip.If(_host == null || _user == null || _key == null, "SF_SSH_* environment variables not set (no test SSH server).");

    private sealed class TestInteraction : IConnectionInteraction, ISshSecretsProvider
    {
        public HostKeyDecision Decision { get; set; } = HostKeyDecision.AcceptAndSave;
        public List<HostKeyInfo> HostKeyPrompts { get; } = [];
        public Queue<string[]> PromptAnswers { get; } = new();
        public List<string> Prompts { get; } = [];
        public Dictionary<string, string> Stored { get; } = [];
        public List<string> Notices { get; } = [];

        public HostKeyDecision ConfirmHostKey(HostKeyInfo info) { HostKeyPrompts.Add(info); return Decision; }
        public string? GetStoredSecret(SecretTarget target) => Stored.GetValueOrDefault(target.VaultKey);
        public void StoreSecret(SecretTarget target, string value) => Stored[target.VaultKey] = value;
        public PromptResult? Prompt(string title, string message, IReadOnlyList<PromptField> fields, bool offerSave)
        {
            Prompts.Add($"{title}: {message}");
            return PromptAnswers.Count > 0 ? new PromptResult(PromptAnswers.Dequeue(), true) : null;
        }
        public void Notify(string message) => Notices.Add(message);
        public (string? Username, string? Password)? GetCredential(Guid credentialId) => null;
        public (string PrivateKey, string? Passphrase)? GetVaultKey(Guid keyId) => null;
    }

    private Session MakeSession() => new()
    {
        Name = "it", Host = _host!, Port = _port, Kind = SessionKind.Ssh,
        Credential = new CredentialRef { Username = _user },
        Ssh = new SshOptions { Auth = AuthMethod.PublicKey, PrivateKeyPath = _key, KeepAliveSeconds = 0 },
    };

    private (SshConnector Connector, TestInteraction Ui, KnownHostsStore Known) MakeConnector()
    {
        var ui = new TestInteraction();
        var known = new KnownHostsStore(Path.Combine(_dir, $"kh-{Guid.NewGuid():N}.json"));
        return (new SshConnector(ui, ui, known), ui, known);
    }

    [SkippableFact]
    public async Task ConnectTrustOnFirstUseThenTrusted()
    {
        RequireServer();
        var (connector, ui, known) = MakeConnector();
        var s = MakeSession();
        using (var ctx = await connector.ConnectAsync(SshEndpoint.BuildChain(s, _ => null, false), null, "t", CancellationToken.None))
        {
            Assert.True(ctx.IsConnected);
            var (code, output, _) = await ctx.RunCommandAsync("echo sf-integration", TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.Equal(0, code);
            Assert.Contains("sf-integration", output);
        }
        Assert.Single(ui.HostKeyPrompts);
        Assert.Equal(HostKeyStatus.Unknown, ui.HostKeyPrompts[0].Status);
        Assert.Single(known.Entries);

        // Second connection: key is trusted, no prompt.
        using (await connector.ConnectAsync(SshEndpoint.BuildChain(s, _ => null, false), null, "t", CancellationToken.None)) { }
        Assert.Single(ui.HostKeyPrompts);
    }

    [SkippableFact]
    public async Task ChangedHostKeyIsDetectedAndRejectable()
    {
        RequireServer();
        var (connector, ui, known) = MakeConnector();
        var s = MakeSession();
        using (await connector.ConnectAsync(SshEndpoint.BuildChain(s, _ => null, false), null, "t", CancellationToken.None)) { }
        var entry = Assert.Single(known.Entries);
        // Simulate a key change: replace the stored key with a different one of the same type.
        known.Remove(entry);
        var fake = new SshWriter().String(entry.KeyType).String(new byte[32]).ToArray();
        known.Trust(entry.Host, entry.Port, entry.KeyType, fake, replaceExisting: false);
        ui.Decision = HostKeyDecision.Reject;
        var ex = await Assert.ThrowsAsync<ConnectionCancelledException>(() => connector.ConnectAsync(SshEndpoint.BuildChain(s, _ => null, false), null, "t", CancellationToken.None));
        Assert.Contains("not accepted", ex.Message);
        Assert.Equal(HostKeyStatus.Changed, ui.HostKeyPrompts[^1].Status);
    }

    [SkippableFact]
    public async Task InteractiveShellRoundTrip()
    {
        RequireServer();
        var (connector, _, _) = MakeConnector();
        var transport = new SshTerminalTransport(connector, MakeSession(), _ => null, false);
        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        transport.DataReceived += d =>
        {
            lock (output) output.Append(Encoding.UTF8.GetString(d.Span));
            if (output.ToString().Contains("SF_MARK_42")) seen.TrySetResult();
        };
        await transport.ConnectAsync(100, 30, CancellationToken.None);
        transport.Resize(120, 40, 0, 0);
        transport.Write(Encoding.UTF8.GetBytes("echo SF_MARK_%d\r".Replace("%d", "42")));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await transport.DisposeAsync();
    }

    [SkippableFact]
    public async Task JumpHostChainThroughSameServer()
    {
        RequireServer();
        var (connector, ui, _) = MakeConnector();
        var s = MakeSession();
        s.Ssh.JumpHosts.Add(new JumpHost { Host = _host!, Port = _port, Auth = AuthMethod.PublicKey, PrivateKeyPath = _key, Credential = new CredentialRef { Username = _user } });
        var chain = SshEndpoint.BuildChain(s, _ => null, false);
        Assert.Equal(2, chain.Count);
        using var ctx = await connector.ConnectAsync(chain, null, "t", CancellationToken.None);
        Assert.Single(ctx.JumpClients);
        // The final hop connects via a loopback tunnel on the jump host.
        Assert.Equal("127.0.0.1", ctx.Client.ConnectionInfo.Host);
        Assert.NotEqual(_port, ctx.Client.ConnectionInfo.Port);
        var (_, output, _) = await ctx.RunCommandAsync("echo via-jump", TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Contains("via-jump", output);
        // Host key was verified against the logical host, once per hop (same server/key → trusted after first).
        Assert.Single(ui.HostKeyPrompts);
        Assert.Equal(_host, ui.HostKeyPrompts[0].Host);
    }

    [SkippableFact]
    public async Task SftpBrowseUploadDownloadRenameDelete()
    {
        RequireServer();
        var (connector, _, _) = MakeConnector();
        using var ctx = await connector.ConnectAsync(SshEndpoint.BuildChain(MakeSession(), _ => null, false), null, "t", CancellationToken.None);
        using var sftp = new SftpService(await ctx.OpenSftpAsync(CancellationToken.None));
        var home = await sftp.GetHomeAsync();
        var dir = SftpService.Combine(home, "sf-it-" + Guid.NewGuid().ToString("N")[..8]);
        await sftp.CreateDirectoryAsync(dir, CancellationToken.None);
        try
        {
            // Queue upload of a local folder with a nested file, then download it again.
            var local = Path.Combine(_dir, "up");
            Directory.CreateDirectory(Path.Combine(local, "sub"));
            var payload = new byte[300_000];
            new Random(7).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(local, "sub", "data.bin"), payload);
            await File.WriteAllTextAsync(Path.Combine(local, "readme.txt"), "hello sftp");

            await using var queue = new TransferQueue(ct => ctx.OpenSftpAsync(ct), 2);
            var done = new TaskCompletionSource<TransferItem>();
            queue.ItemCompleted += i => done.TrySetResult(i);
            queue.Enqueue(new TransferItem { Direction = TransferDirection.Upload, LocalPath = local, RemotePath = SftpService.Combine(dir, "up"), IsDirectory = true });
            var up = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferStatus.Completed, up.Status);
            Assert.Equal(2, up.FilesDone);

            var listing = await sftp.ListAsync(SftpService.Combine(dir, "up"), CancellationToken.None);
            Assert.Contains(listing, e => e.Name == "sub" && e.IsDirectory);
            Assert.Contains(listing, e => e.Name == "readme.txt" && e.Size == 10);

            var done2 = new TaskCompletionSource<TransferItem>();
            queue.ItemCompleted += i => { if (i.Direction == TransferDirection.Download) done2.TrySetResult(i); };
            var downTo = Path.Combine(_dir, "down");
            var upEntry = (await sftp.ListAsync(dir, CancellationToken.None)).Single(e => e.Name == "up");
            queue.Enqueue(new TransferItem { Direction = TransferDirection.Download, LocalPath = downTo, RemotePath = upEntry.FullPath, IsDirectory = true });
            var down = await done2.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferStatus.Completed, down.Status);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(downTo, "sub", "data.bin")));
            Assert.Equal("hello sftp", await File.ReadAllTextAsync(Path.Combine(downTo, "readme.txt")));

            // Safe overwrite via temp + rename, then rename & delete.
            var file = SftpService.Combine(dir, "edit.txt");
            await sftp.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("v1"), CancellationToken.None);
            await sftp.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("v2-longer"), CancellationToken.None);
            Assert.Equal("v2-longer", Encoding.UTF8.GetString(await sftp.ReadAllBytesAsync(file, 1000, CancellationToken.None)));
            var renamed = SftpService.Combine(dir, "renamed.txt");
            await sftp.RenameAsync(file, renamed, CancellationToken.None);
            Assert.True(await sftp.ExistsAsync(renamed, CancellationToken.None));
            Assert.False(await sftp.ExistsAsync(file, CancellationToken.None));
        }
        finally
        {
            var entry = (await sftp.ListAsync(home, CancellationToken.None)).Single(e => e.FullPath == dir || e.Name == Path.GetFileName(dir));
            await sftp.DeleteAsync(entry, CancellationToken.None);
        }
        Assert.False(await sftp.ExistsAsync(dir, CancellationToken.None));
    }

    private static (TcpListener Listener, Task Server) StartEchoServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var t = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch (ObjectDisposedException) { return; } catch (SocketException) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[4096];
                        int n;
                        while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
                    }
                });
            }
        });
        return (l, t);
    }

    [SkippableFact]
    public async Task LocalAndDynamicPortForwarding()
    {
        RequireServer();
        var (listener, _) = StartEchoServer();
        int echoPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var (connector, _, _) = MakeConnector();
            using var ctx = await connector.ConnectAsync(SshEndpoint.BuildChain(MakeSession(), _ => null, false), null, "t", CancellationToken.None);
            using var mgr = new PortForwardManager(ctx.Client);

            var local = mgr.Start(new PortForward { Kind = ForwardKind.Local, BindAddress = "127.0.0.1", BindPort = 0, TargetHost = "127.0.0.1", TargetPort = (uint)echoPort });
            Assert.Equal(ForwardStatus.Running, local.Status);
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, (int)local.EffectiveBindPort);
                var s = c.GetStream();
                await s.WriteAsync("ping-local"u8.ToArray());
                var buf = new byte[10];
                await s.ReadExactlyAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("ping-local", Encoding.ASCII.GetString(buf));
            }

            // Dynamic (SOCKS5) forward on a free port.
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int socksPort = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var dyn = mgr.Start(new PortForward { Kind = ForwardKind.Dynamic, BindAddress = "127.0.0.1", BindPort = (uint)socksPort });
            Assert.Equal(ForwardStatus.Running, dyn.Status);
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, socksPort);
                var s = c.GetStream();
                await s.WriteAsync(new byte[] { 5, 1, 0 });                       // greeting: no auth
                var greet = new byte[2]; await s.ReadExactlyAsync(greet);
                Assert.Equal(new byte[] { 5, 0 }, greet);
                var req = new byte[10] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 0 };          // CONNECT 127.0.0.1:echoPort
                BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(8), (ushort)echoPort);
                await s.WriteAsync(req);
                var reply = new byte[10]; await s.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, reply[1]);                                          // succeeded
                await s.WriteAsync("ping-socks"u8.ToArray());
                var buf = new byte[10]; await s.ReadExactlyAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("ping-socks", Encoding.ASCII.GetString(buf));
            }
        }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Authenticates through the Windows OpenSSH agent named pipe, served here by a minimal in-process agent
    /// (the real ssh-agent service may be disabled). Skipped if something already owns the pipe.
    /// </summary>
    [SkippableFact]
    public async Task AgentAuthenticationThroughNamedPipe()
    {
        RequireServer();
        Skip.If(File.Exists(@"\\.\pipe\openssh-ssh-agent"), "A real SSH agent already owns the pipe.");
        var alg = new Renci.SshNet.PrivateKeyFile(_key!).HostKeyAlgorithms.First();
        using var cts = new CancellationTokenSource();
        int signRequests = 0;

        static byte[] Frame(params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            var f = new byte[body.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(f, (uint)body.Length);
            body.CopyTo(f, 4);
            return f;
        }
        static byte[] Str(byte[] b) { var r = new byte[b.Length + 4]; BinaryPrimitives.WriteUInt32BigEndian(r, (uint)b.Length); b.CopyTo(r, 4); return r; }
        static byte[] U32(uint v) { var r = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(r, v); return r; }

        var agentTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await using var server = new System.IO.Pipes.NamedPipeServerStream("openssh-ssh-agent", System.IO.Pipes.PipeDirection.InOut, 4, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                try { await server.WaitForConnectionAsync(cts.Token); } catch (OperationCanceledException) { return; }
                try
                {
                    var len = new byte[4]; await server.ReadExactlyAsync(len);
                    var msg = new byte[BinaryPrimitives.ReadUInt32BigEndian(len)]; await server.ReadExactlyAsync(msg);
                    byte[] reply;
                    if (msg[0] == 11) reply = Frame([12], U32(1), Str(alg.Data), Str("test-key"u8.ToArray()));
                    else if (msg[0] == 13)
                    {
                        int keyLen = (int)BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(1));
                        int dataOff = 1 + 4 + keyLen;
                        int dataLen = (int)BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(dataOff));
                        var sig = alg.Sign(msg.AsSpan(dataOff + 4, dataLen).ToArray());
                        Interlocked.Increment(ref signRequests);
                        reply = Frame([14], Str(sig));
                    }
                    else reply = Frame([5]);
                    await server.WriteAsync(reply);
                    await server.FlushAsync();
                }
                catch (IOException) { }
            }
        });

        try
        {
            var session = MakeSession();
            session.Ssh = new SshOptions { Auth = AuthMethod.Agent, KeepAliveSeconds = 0 };
            var (connector, _, _) = MakeConnector();
            using var ctx = await connector.ConnectAsync(SshEndpoint.BuildChain(session, _ => null, true), null, "t", CancellationToken.None);
            Assert.True(ctx.Client.IsConnected);
            Assert.True(signRequests >= 1, "the agent was not asked to sign");
        }
        finally
        {
            cts.Cancel();
            try { using var poke = new System.IO.Pipes.NamedPipeClientStream(".", "openssh-ssh-agent"); poke.Connect(500); } catch (Exception) { }
            await agentTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [SkippableFact]
    public async Task RemotePortForwarding()
    {
        RequireServer();
        var (listener, _) = StartEchoServer();
        int echoPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var (connector, _, _) = MakeConnector();
            using var ctx = await connector.ConnectAsync(SshEndpoint.BuildChain(MakeSession(), _ => null, false), null, "t", CancellationToken.None);
            using var mgr = new PortForwardManager(ctx.Client);
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int remotePort = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();

            var remote = mgr.Start(new PortForward { Kind = ForwardKind.Remote, BindAddress = "127.0.0.1", BindPort = (uint)remotePort, TargetHost = "127.0.0.1", TargetPort = (uint)echoPort });
            Assert.Equal(ForwardStatus.Running, remote.Status);
            // The server listens on remotePort and tunnels back to our echo server; connect to it from this machine (same host as sshd).
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, remotePort);
            var s = c.GetStream();
            await s.WriteAsync("ping-remote"u8.ToArray());
            var buf = new byte[11];
            await s.ReadExactlyAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ping-remote", Encoding.ASCII.GetString(buf));
        }
        finally { listener.Stop(); }
    }

    [SkippableFact]
    public async Task EncryptedKeyGeneratedByUsAuthenticatesWithPassphrasePrompt()
    {
        RequireServer();
        Skip.If(_authKeys == null, "SF_SSH_AUTHKEYS not set.");
        var key = SshKeys.Generate(SshKeyType.Ed25519, "sf-it-encrypted", "s3cret-pass");
        var keyPath = Path.Combine(_dir, "enc_key");
        await File.WriteAllTextAsync(keyPath, key.PrivateKeyPem);
        var original = await File.ReadAllTextAsync(_authKeys!);
        await File.AppendAllTextAsync(_authKeys!, Environment.NewLine + key.PublicKeyLine + Environment.NewLine);
        try
        {
            var (connector, ui, _) = MakeConnector();
            ui.PromptAnswers.Enqueue(["s3cret-pass"]);
            var s = MakeSession();
            s.Ssh.PrivateKeyPath = keyPath;
            s.Credential.SavePassword = true;
            using var ctx = await connector.ConnectAsync(SshEndpoint.BuildChain(s, _ => null, false), null, "t", CancellationToken.None);
            Assert.True(ctx.IsConnected);
            Assert.Contains(ui.Prompts, p => p.StartsWith("Key passphrase", StringComparison.Ordinal));
            // The passphrase was offered for saving and stored under the session scope.
            Assert.Equal("s3cret-pass", ui.Stored[$"session:{s.Id}:passphrase"]);
        }
        finally { await File.WriteAllTextAsync(_authKeys!, original); }
    }

    [SkippableFact]
    public async Task WrongKeyFailsWithClearMessage()
    {
        RequireServer();
        var other = SshKeys.Generate(SshKeyType.Ed25519, "not-authorized", null);
        var path = Path.Combine(_dir, "wrong");
        await File.WriteAllTextAsync(path, other.PrivateKeyPem);
        var (connector, _, _) = MakeConnector();
        var s = MakeSession();
        s.Ssh.PrivateKeyPath = path;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(SshEndpoint.BuildChain(s, _ => null, false), null, "t", CancellationToken.None));
        Assert.Contains("Authentication failed", ex.Message);
    }
}
