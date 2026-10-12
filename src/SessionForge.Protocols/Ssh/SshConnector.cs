using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SessionForge.Core.Models;
using SessionForge.Core.Security;

namespace SessionForge.Protocols.Ssh;

/// <summary>Credential/key lookups the connector needs beyond simple secrets.</summary>
public interface ISshSecretsProvider
{
    (string? Username, string? Password)? GetCredential(Guid credentialId);
    (string PrivateKey, string? Passphrase)? GetVaultKey(Guid keyId);
}

/// <summary>Authentication material that worked for an endpoint; reused for extra channels (SFTP, tunnels).</summary>
internal sealed class AuthMaterial
{
    public required string Username { get; init; }
    public string? Password { get; set; }
    public List<IPrivateKeySource> Keys { get; } = [];
    public AgentKeySource? Agent { get; set; }
    public SecretTarget? PasswordTarget { get; set; }
    public bool SavePassword { get; set; }
    public SecretTarget? PassphraseTarget { get; set; }
    public string? Passphrase { get; set; }
    public bool SavePassphrase { get; set; }
}

/// <summary>
/// Establishes SSH connections, including multi-hop jump host chains (each hop tunnelled through a local
/// port forward on the previous hop), proxies, host key verification (TOFU against our known hosts store)
/// and interactive authentication with vault integration.
/// </summary>
public sealed class SshConnector
{
    private readonly IConnectionInteraction _ui;
    private readonly ISshSecretsProvider _secrets;
    private readonly KnownHostsStore _knownHosts;

    public SshConnector(IConnectionInteraction ui, ISshSecretsProvider secrets, KnownHostsStore knownHosts)
    {
        _ui = ui;
        _secrets = secrets;
        _knownHosts = knownHosts;
    }

    /// <summary>When false, unknown host keys are accepted automatically (not recommended).</summary>
    public bool StrictHostKeyChecking { get; set; } = true;

    public event Action<string>? Progress;

    /// <summary>Connects through the chain; the last endpoint is the target. Caller owns the returned context.</summary>
    public async Task<SshConnectionContext> ConnectAsync(IReadOnlyList<SshEndpoint> chain, ProxySettings? proxy, string proxySecretScope, CancellationToken ct)
    {
        if (chain.Count == 0) throw new ArgumentException("Empty SSH chain.", nameof(chain));
        var ctx = new SshConnectionContext(this);
        try
        {
            string? proxyPassword = ResolveProxyPassword(proxy, proxySecretScope);
            string connectHost = chain[0].Host;
            int connectPort = chain[0].Port;
            for (int i = 0; i < chain.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ep = chain[i];
                bool last = i == chain.Count - 1;
                Progress?.Invoke(i == 0 ? $"Connecting to {ep.Display}…" : $"Connecting to {ep.Display} via jump host {chain[i - 1].Display}…");
                var hopProxy = i == 0 ? proxy : null;
                var (client, material) = await ConnectHopAsync(ep, connectHost, connectPort, hopProxy, hopProxy == null ? null : proxyPassword, ctx, ct).ConfigureAwait(false);
                if (last)
                {
                    ctx.SetTarget(client, ep, material, connectHost, connectPort, hopProxy, hopProxy == null ? null : proxyPassword);
                }
                else
                {
                    ctx.AddHop(client);
                    var next = chain[i + 1];
                    // Ephemeral local port on loopback only; tunnels to the next hop through this one.
                    var fwd = new ForwardedPortLocal("127.0.0.1", 0, next.Host, (uint)next.Port);
                    client.AddForwardedPort(fwd);
                    fwd.Start();
                    ctx.AddHopForward(fwd);
                    connectHost = "127.0.0.1";
                    connectPort = (int)fwd.BoundPort;
                }
            }
            return ctx;
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
    }

    internal async Task<(SshClient, AuthMaterial)> ConnectHopAsync(SshEndpoint ep, string connectHost, int connectPort, ProxySettings? proxy, string? proxyPassword, SshConnectionContext ctx, CancellationToken ct)
    {
        EnsureUsername(ep);
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            var material = BuildMaterial(ep, retry: attempt > 1);
            var client = CreateClient<SshClient>(ep, connectHost, connectPort, proxy, proxyPassword, material, ctx, ci => new SshClient(ci));
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                PersistSecrets(material);
                return (client, material);
            }
            catch (SshAuthenticationException) when (attempt < maxAttempts && CanRetry(ep, material))
            {
                client.Dispose();
                _ui.Notify($"Access denied for {ep.Display}.");
            }
            catch (Exception ex)
            {
                client.Dispose();
                throw Translate(ex, ep, ctx);
            }
        }
    }

    private static bool CanRetry(SshEndpoint ep, AuthMaterial m) =>
        m.PasswordTarget != null || (ep.Auth == AuthMethod.PublicKey && m.PassphraseTarget != null);

    internal T CreateClient<T>(SshEndpoint ep, string connectHost, int connectPort, ProxySettings? proxy, string? proxyPassword,
        AuthMaterial material, SshConnectionContext ctx, Func<ConnectionInfo, T> factory) where T : BaseClient
    {
        var methods = CreateMethods(ep, material);
        ConnectionInfo ci = proxy is { Kind: not ProxyKind.None }
            ? new ConnectionInfo(connectHost, connectPort, material.Username, MapProxy(proxy.Kind), proxy.Host, proxy.Port, proxy.Username ?? string.Empty, proxyPassword ?? string.Empty, methods)
            : new ConnectionInfo(connectHost, connectPort, material.Username, methods);
        ci.Timeout = TimeSpan.FromSeconds(Math.Clamp(ep.TimeoutSeconds, 3, 300));
        ci.Encoding = Encoding.UTF8;
        ApplyAlgorithmPreferences(ci, ep);
        ci.AuthenticationBanner += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.BannerMessage)) _ui.Notify(e.BannerMessage.TrimEnd());
        };
        var client = factory(ci);
        if (ep.KeepAliveSeconds > 0) client.KeepAliveInterval = TimeSpan.FromSeconds(ep.KeepAliveSeconds);
        client.HostKeyReceived += (_, e) => e.CanTrust = VerifyHostKey(ep, e.HostKeyName, e.HostKey, ctx);
        return client;
    }

    private static ProxyTypes MapProxy(ProxyKind k) => k switch
    {
        ProxyKind.Socks4 => ProxyTypes.Socks4,
        ProxyKind.Socks5 => ProxyTypes.Socks5,
        ProxyKind.Http => ProxyTypes.Http,
        _ => ProxyTypes.None,
    };

    private static void ApplyAlgorithmPreferences(ConnectionInfo ci, SshEndpoint ep)
    {
        static void Filter<TV>(IDictionary<string, TV> dict, IReadOnlyList<string> allowed)
        {
            if (allowed.Count == 0) return;
            var keep = allowed.Where(dict.ContainsKey).ToHashSet(StringComparer.Ordinal);
            if (keep.Count == 0) return; // never leave the list empty — would make every connection fail
            foreach (var k in dict.Keys.ToList()) if (!keep.Contains(k)) dict.Remove(k);
        }
        Filter(ci.KeyExchangeAlgorithms, ep.KexAlgorithms);
        Filter(ci.Encryptions, ep.Ciphers);
        Filter(ci.HostKeyAlgorithms, ep.HostKeyAlgorithms);
        if (!ep.Compression)
        {
            foreach (var k in ci.CompressionAlgorithms.Keys.ToList()) if (k != "none") ci.CompressionAlgorithms.Remove(k);
        }
    }

    private bool VerifyHostKey(SshEndpoint ep, string keyType, byte[] keyBlob, SshConnectionContext ctx)
    {
        // Keys already accepted during this connection context (e.g. "accept once") are re-accepted for extra channels.
        if (ctx.IsAccepted(ep.Host, ep.Port, keyBlob)) return true;
        var status = _knownHosts.Check(ep.Host, ep.Port, keyType, keyBlob, out var previous);
        if (status == HostKeyStatus.Trusted) { ctx.Accept(ep.Host, ep.Port, keyBlob); return true; }
        if (status == HostKeyStatus.Unknown && !StrictHostKeyChecking)
        {
            _knownHosts.Trust(ep.Host, ep.Port, keyType, keyBlob, replaceExisting: false);
            ctx.Accept(ep.Host, ep.Port, keyBlob);
            return true;
        }
        var info = new HostKeyInfo(ep.Host, ep.Port, keyType, keyBlob, KnownHostsStore.Fingerprint(keyBlob),
            KnownHostsStore.FingerprintMd5(keyBlob), status, previous);
        var decision = _ui.ConfirmHostKey(info);
        switch (decision)
        {
            case HostKeyDecision.AcceptAndSave:
                _knownHosts.Trust(ep.Host, ep.Port, keyType, keyBlob, replaceExisting: status == HostKeyStatus.Changed);
                ctx.Accept(ep.Host, ep.Port, keyBlob);
                return true;
            case HostKeyDecision.AcceptOnce:
                ctx.Accept(ep.Host, ep.Port, keyBlob);
                return true;
            default:
                ctx.HostKeyRejected = true;
                return false;
        }
    }

    private Exception Translate(Exception ex, SshEndpoint ep, SshConnectionContext ctx)
    {
        if (ctx.HostKeyRejected) return new ConnectionCancelledException($"Host key for {ep.Host} was not accepted. Connection aborted.");
        if (ex is ConnectionCancelledException or OperationCanceledException) return ex;
        return ex switch
        {
            SshAuthenticationException => new SshFailureException($"Authentication failed for {ep.Display}: {ex.Message}", ex),
            SocketException se => new SshFailureException($"Could not connect to {ep.Host}:{ep.Port} — {se.Message}", ex),
            ProxyException => new SshFailureException($"Proxy error while connecting to {ep.Display}: {ex.Message}", ex),
            SshOperationTimeoutException => new SshFailureException($"Timed out connecting to {ep.Display}.", ex),
            SshConnectionException => new SshFailureException($"SSH connection to {ep.Display} failed: {ex.Message}", ex),
            _ => ex,
        };
    }

    private void EnsureUsername(SshEndpoint ep)
    {
        if (!string.IsNullOrEmpty(ep.Username)) return;
        if (ep.CredentialId is { } cid && _secrets.GetCredential(cid) is { Username: { Length: > 0 } u })
        {
            ep.Username = u;
            return;
        }
        var r = _ui.Prompt($"Login to {ep.Host}", $"Username for {ep.Host}:{ep.Port}", [new PromptField("Login as", false, Environment.UserName)], offerSave: false)
            ?? throw new ConnectionCancelledException("Login cancelled.");
        ep.Username = r.Values[0];
        if (string.IsNullOrWhiteSpace(ep.Username)) throw new ConnectionCancelledException("No username given.");
    }

    private string? ResolveProxyPassword(ProxySettings? proxy, string scope)
    {
        if (proxy is not { Kind: not ProxyKind.None, HasPassword: true }) return null;
        var target = new SecretTarget($"{scope}:proxy", $"proxy {proxy.Host}", proxy.Username);
        var stored = _ui.GetStoredSecret(target); // HasPassword already implies it was saved
        if (stored != null) return stored;
        var r = _ui.Prompt("Proxy authentication", $"Password for proxy {proxy.Username}@{proxy.Host}:{proxy.Port}", [new PromptField("Password", true)], offerSave: true)
            ?? throw new ConnectionCancelledException("Proxy login cancelled.");
        if (r.Save) _ui.StoreSecret(target, r.Values[0]);
        return r.Values[0];
    }

    private AuthMaterial BuildMaterial(SshEndpoint ep, bool retry)
    {
        var m = new AuthMaterial { Username = ep.Username! };

        // Keys
        if (ep.Auth == AuthMethod.PublicKey)
        {
            if (ep.VaultKeyId is { } kid)
            {
                var vk = _secrets.GetVaultKey(kid) ?? throw new SshFailureException($"The key referenced by this session is not in the vault (or the vault is locked).");
                m.Keys.Add(LoadKey(() => new MemoryStream(Encoding.UTF8.GetBytes(vk.PrivateKey)), vk.Passphrase, ep, $"vault key", m, retry));
            }
            else if (!string.IsNullOrWhiteSpace(ep.PrivateKeyPath))
            {
                var path = Environment.ExpandEnvironmentVariables(ep.PrivateKeyPath);
                if (!File.Exists(path)) throw new SshFailureException($"Private key file not found: {path}");
                m.Keys.Add(LoadKey(() => File.OpenRead(path), null, ep, path, m, retry));
            }
        }

        if (ep.TryAgent && ep.Auth != AuthMethod.Password)
        {
            var agents = SshAgentClient.Detect();
            if (agents.Count > 0)
            {
                var src = new AgentKeySource(agents);
                if (!src.IsEmpty) m.Agent = src;
            }
            if (ep.Auth == AuthMethod.Agent && m.Agent == null)
                _ui.Notify("No SSH agent with keys was found (Windows OpenSSH agent or Pageant). Falling back to password.");
        }

        // Password (only fetched/prompted when password auth is selected, or as the fallback for agent with no keys).
        bool wantPassword = ep.Auth is AuthMethod.Password or AuthMethod.KeyboardInteractive || (ep.Auth == AuthMethod.Agent && m.Agent == null);
        if (wantPassword)
        {
            if (ep.CredentialId is { } cid && !retry && _secrets.GetCredential(cid) is { Password: { } cpw })
            {
                m.Password = cpw;
            }
            else
            {
                var target = new SecretTarget($"{ep.SecretScope}:password", ep.Display, ep.Username);
                m.PasswordTarget = target;
                // Only consult the vault when the session says a password was saved (avoids needless unlock prompts).
                var stored = retry || !ep.SavePasswordByDefault ? null : _ui.GetStoredSecret(target);
                if (stored != null) m.Password = stored;
                else if (ep.Auth != AuthMethod.KeyboardInteractive)
                {
                    var r = _ui.Prompt($"Password for {ep.Display}", retry ? "Access denied. Please try again." : $"{ep.Username}@{ep.Host}'s password:",
                        [new PromptField("Password", true)], offerSave: true) ?? throw new ConnectionCancelledException("Login cancelled.");
                    m.Password = r.Values[0];
                    m.SavePassword = r.Save;
                }
            }
        }
        return m;
    }

    private IPrivateKeySource LoadKey(Func<Stream> open, string? knownPassphrase, SshEndpoint ep, string label, AuthMaterial m, bool retry)
    {
        var target = new SecretTarget($"{ep.SecretScope}:passphrase", $"key {label}", ep.Username);
        string? pass = knownPassphrase ?? (retry || !ep.SavePasswordByDefault ? null : _ui.GetStoredSecret(target));
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var s = open();
                var key = pass == null ? new PrivateKeyFile(s) : new PrivateKeyFile(s, pass);
                if (pass != null && knownPassphrase == null) { m.PassphraseTarget = target; m.Passphrase = pass; }
                return key;
            }
            catch (Exception ex) when (ex is SshPassPhraseNullOrEmptyException or SshException or System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                if (ex is SshException && ex is not SshPassPhraseNullOrEmptyException && pass == null) throw new SshFailureException($"Could not read private key {label}: {ex.Message}", ex);
                var r = _ui.Prompt("Key passphrase", pass == null ? $"Passphrase for {label}:" : $"Wrong passphrase for {label}. Try again:",
                    [new PromptField("Passphrase", true)], offerSave: true) ?? throw new ConnectionCancelledException("Passphrase entry cancelled.");
                pass = r.Values[0];
                m.SavePassphrase = r.Save;
            }
        }
        throw new SshFailureException($"Could not decrypt private key {label}.");
    }

    private AuthenticationMethod[] CreateMethods(SshEndpoint ep, AuthMaterial m)
    {
        var list = new List<AuthenticationMethod>();
        if (m.Keys.Count > 0) list.Add(new PrivateKeyAuthenticationMethod(m.Username, m.Keys.ToArray()));
        if (m.Agent != null) list.Add(new PrivateKeyAuthenticationMethod(m.Username, m.Agent));
        if (m.Password != null) list.Add(new PasswordAuthenticationMethod(m.Username, m.Password));

        // Keyboard-interactive: answers password prompts from material, asks the user for anything else (OTP, 2FA).
        var kbd = new KeyboardInteractiveAuthenticationMethod(m.Username);
        bool passwordUsed = false;
        kbd.AuthenticationPrompt += (_, e) =>
        {
            var unanswered = new List<AuthenticationPrompt>();
            foreach (var p in e.Prompts)
            {
                if (!passwordUsed && m.Password != null && p.Request.Contains("password", StringComparison.OrdinalIgnoreCase) && !p.IsEchoed)
                {
                    p.Response = m.Password;
                    passwordUsed = true;
                }
                else unanswered.Add(p);
            }
            if (unanswered.Count == 0) return;
            var r = _ui.Prompt(string.IsNullOrWhiteSpace(e.Username) ? ep.Display : $"{e.Username}@{ep.Host}",
                string.IsNullOrWhiteSpace(e.Instruction) ? "The server requests additional authentication." : e.Instruction,
                unanswered.Select(p => new PromptField(p.Request.Trim(), !p.IsEchoed)).ToList(), offerSave: false);
            for (int i = 0; i < unanswered.Count; i++) unanswered[i].Response = r?.Values[i] ?? string.Empty;
            if (r != null && m.Password == null && unanswered.Count == 1 && !unanswered[0].IsEchoed && unanswered[0].Request.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                m.Password = r.Values[0]; // remember for SFTP/extra channels in this context
            }
        };
        list.Add(kbd);
        return list.ToArray();
    }

    private void PersistSecrets(AuthMaterial m)
    {
        if (m.SavePassword && m.PasswordTarget != null && m.Password != null) _ui.StoreSecret(m.PasswordTarget, m.Password);
        if (m.SavePassphrase && m.PassphraseTarget != null && m.Passphrase != null) _ui.StoreSecret(m.PassphraseTarget, m.Passphrase);
        m.SavePassword = m.SavePassphrase = false;
    }
}

public sealed class SshFailureException : Exception
{
    public SshFailureException(string message) : base(message) { }
    public SshFailureException(string message, Exception inner) : base(message, inner) { }
}
