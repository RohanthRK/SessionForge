using Renci.SshNet;
using SessionForge.Core.Models;

namespace SessionForge.Protocols.Ssh;

/// <summary>
/// A live SSH connection to a target, including any jump-host clients and their tunnel forwards.
/// Can open additional authenticated clients (SFTP, extra shells) to the same target over the same chain
/// without prompting again. Disposing tears everything down in reverse order.
/// </summary>
public sealed class SshConnectionContext : IDisposable
{
    private readonly SshConnector _connector;
    private readonly List<SshClient> _hops = [];
    private readonly List<ForwardedPortLocal> _hopForwards = [];
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
    private readonly List<BaseClient> _extraClients = [];
    private readonly object _lock = new();
    private SshEndpoint? _endpoint;
    private AuthMaterial? _material;
    private string _connectHost = string.Empty;
    private int _connectPort;
    private ProxySettings? _proxy;
    private string? _proxyPassword;
    private bool _disposed;

    internal SshConnectionContext(SshConnector connector) => _connector = connector;

    public SshClient Client { get; private set; } = null!;
    public SshEndpoint Endpoint => _endpoint!;
    public IReadOnlyList<SshClient> JumpClients => _hops;
    internal bool HostKeyRejected { get; set; }

    /// <summary>Raised (once) when the target or any hop disconnects unexpectedly.</summary>
    public event Action<string>? ConnectionLost;

    public bool IsConnected => !_disposed && Client?.IsConnected == true && _hops.All(h => h.IsConnected);

    public string ServerVersion => Client?.ConnectionInfo.ServerVersion ?? string.Empty;

    public string Describe()
    {
        if (Client == null) return string.Empty;
        var ci = Client.ConnectionInfo;
        var via = _hops.Count == 0 ? string.Empty : $" via {string.Join(" → ", _hops.Select(h => h.ConnectionInfo.Host == "127.0.0.1" ? "(tunnel)" : h.ConnectionInfo.Host))}";
        return $"{ci.CurrentKeyExchangeAlgorithm}, {ci.CurrentServerEncryption}, {ci.CurrentHostKeyAlgorithm}{via}";
    }

    internal bool IsAccepted(string host, int port, byte[] blob) { lock (_lock) return _accepted.Contains(Key(host, port, blob)); }
    internal void Accept(string host, int port, byte[] blob) { lock (_lock) _accepted.Add(Key(host, port, blob)); }
    private static string Key(string host, int port, byte[] blob) => $"{host.ToLowerInvariant()}:{port}:{Convert.ToBase64String(blob)}";

    internal void AddHop(SshClient client)
    {
        _hops.Add(client);
        client.ErrorOccurred += (_, e) => RaiseLost($"Jump host {client.ConnectionInfo.Host}: {e.Exception.Message}");
    }

    internal void AddHopForward(ForwardedPortLocal fwd) => _hopForwards.Add(fwd);

    internal void SetTarget(SshClient client, SshEndpoint ep, AuthMaterial material, string connectHost, int connectPort, ProxySettings? proxy, string? proxyPassword)
    {
        Client = client;
        _endpoint = ep;
        _material = material;
        _connectHost = connectHost;
        _connectPort = connectPort;
        _proxy = proxy;
        _proxyPassword = proxyPassword;
        client.ErrorOccurred += (_, e) => RaiseLost(e.Exception.Message);
    }

    private int _lostRaised;
    private void RaiseLost(string reason)
    {
        if (_disposed || Interlocked.Exchange(ref _lostRaised, 1) == 1) return;
        ConnectionLost?.Invoke(reason);
    }

    /// <summary>Opens a new SFTP client to the same target (separate TCP/SSH connection over the same jump chain).</summary>
    public async Task<SftpClient> OpenSftpAsync(CancellationToken ct)
    {
        var c = CreateExtra(ci => new SftpClient(ci));
        try
        {
            await c.ConnectAsync(ct).ConfigureAwait(false);
            c.OperationTimeout = TimeSpan.FromSeconds(60);
            return c;
        }
        catch { c.Dispose(); throw; }
    }

    /// <summary>Opens an additional SSH client (e.g. for a standalone tunnel or a duplicated tab).</summary>
    public async Task<SshClient> OpenAdditionalClientAsync(CancellationToken ct)
    {
        var c = CreateExtra(ci => new SshClient(ci));
        try { await c.ConnectAsync(ct).ConfigureAwait(false); return c; }
        catch { c.Dispose(); throw; }
    }

    private T CreateExtra<T>(Func<ConnectionInfo, T> factory) where T : BaseClient
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_material == null || _endpoint == null) throw new InvalidOperationException("Not connected.");
        var c = _connector.CreateClient(_endpoint, _connectHost, _connectPort, _proxy, _proxyPassword, _material, this, factory);
        lock (_lock) _extraClients.Add(c);
        return c;
    }

    /// <summary>Runs a command on the target and returns stdout (used for e.g. remote monitoring or "follow folder").</summary>
    public async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var cmd = Client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        await cmd.ExecuteAsync(ct).ConfigureAwait(false);
        return (cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<BaseClient> extras;
        lock (_lock) { extras = [.. _extraClients]; _extraClients.Clear(); }
        foreach (var e in extras) SafeDispose(e);
        if (Client != null) SafeDispose(Client);
        for (int i = _hops.Count - 1; i >= 0; i--)
        {
            if (i < _hopForwards.Count) { try { _hopForwards[i].Stop(); } catch (Exception) { } }
            SafeDispose(_hops[i]);
        }
        if (_material != null)
            foreach (var k in _material.Keys.OfType<IDisposable>()) k.Dispose();
    }

    private static void SafeDispose(BaseClient c)
    {
        try { if (c.IsConnected) c.Disconnect(); } catch (Exception) { }
        try { c.Dispose(); } catch (Exception) { }
    }
}
