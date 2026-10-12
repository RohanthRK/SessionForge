using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SessionForge.Core.Models;
using Session = SessionForge.Core.Models.Session;

namespace SessionForge.Protocols.Ssh;

/// <summary>Interactive SSH shell (PTY) over a <see cref="SshConnectionContext"/>.</summary>
public sealed class SshTerminalTransport : ITerminalTransport
{
    private readonly SshConnector _connector;
    private readonly Session _session;
    private readonly Func<Guid, Session?> _resolve;
    private readonly bool _useAgent;
    private ShellStream? _shell;
    private Thread? _reader;
    private int _closed;
    private readonly object _writeLock = new();

    public SshTerminalTransport(SshConnector connector, Session session, Func<Guid, Session?> resolveSession, bool useAgent)
    {
        _connector = connector;
        _session = session;
        _resolve = resolveSession;
        _useAgent = useAgent;
        DisplayName = session.Describe();
    }

    public string DisplayName { get; }
    public TransportState State { get; private set; }
    public SshConnectionContext? Context { get; private set; }
    public PortForwardManager? Forwards { get; private set; }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string?>? Closed;
    public event Action<string>? Notice;

    public async Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        State = TransportState.Connecting;
        void OnProgress(string m) => Notice?.Invoke(m);
        _connector.Progress += OnProgress;
        try
        {
            var chain = SshEndpoint.BuildChain(_session, _resolve, _useAgent);
            Context = await _connector.ConnectAsync(chain, _session.Ssh.Proxy, $"session:{_session.Id}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            State = TransportState.Failed;
            throw;
        }
        finally { _connector.Progress -= OnProgress; }

        Context.ConnectionLost += reason => Close(reason);
        Notice?.Invoke($"Connected to {Context.Endpoint.Display} ({Context.ServerVersion}; {Context.Describe()})");

        var modes = new Dictionary<TerminalModes, uint>
        {
            [TerminalModes.VERASE] = _session.Terminal.BackspaceSendsControlH ? 8u : 127u,
            [TerminalModes.IUTF8] = 1,
        };
        _shell = Context.Client.CreateShellStream(_session.Ssh.TerminalType, (uint)columns, (uint)rows, 0, 0, 65536, modes);
        _shell.Closed += (_, _) => Close(null);
        _shell.ErrorOccurred += (_, e) => Close(e.Exception.Message);

        Forwards = new PortForwardManager(Context.Client);
        Forwards.Notice += m => Notice?.Invoke(m);
        Forwards.StartAll(_session.Ssh.Forwards);

        if (_session.Ssh.AgentForwarding) Notice?.Invoke("Note: SSH agent forwarding is not supported by this SSH engine and was not enabled.");
        if (_session.Ssh.X11Forwarding) Notice?.Invoke("Note: X11 forwarding is not supported and was not enabled.");

        State = TransportState.Connected;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ssh-read {_session.Host}" };
        _reader.Start();

        // Startup actions run inside the interactive shell, like typing them.
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_session.Ssh.StartupDirectory))
            sb.Append("cd '").Append(_session.Ssh.StartupDirectory.Replace("'", "'\\''")).Append("'\n");
        if (!string.IsNullOrWhiteSpace(_session.Ssh.RemoteCommand))
        {
            sb.Append(_session.Ssh.RemoteCommand);
            if (!_session.Ssh.StayAfterCommand) sb.Append("; exit");
            sb.Append('\n');
        }
        if (sb.Length > 0) Write(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private void ReadLoop()
    {
        var buf = new byte[32 * 1024];
        try
        {
            while (Volatile.Read(ref _closed) == 0)
            {
                int n = _shell!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf.AsMemory(0, n));
            }
            Close(null);
        }
        catch (ObjectDisposedException) { Close(null); }
        catch (Exception ex) { Close(ex.Message); }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var shell = _shell;
        if (shell == null || State != TransportState.Connected) return;
        try
        {
            lock (_writeLock)
            {
                shell.Write(data);
                shell.Flush();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or InvalidOperationException)
        {
            Close(ex.Message);
        }
    }

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        try { _shell?.ChangeWindowSize((uint)columns, (uint)rows, (uint)Math.Max(0, pixelWidth), (uint)Math.Max(0, pixelHeight)); }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException) { }
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        State = reason == null ? TransportState.Closed : TransportState.Failed;
        Closed?.Invoke(reason);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        State = TransportState.Closed;
        try { Forwards?.Dispose(); } catch (Exception) { }
        try { _shell?.Dispose(); } catch (Exception) { }
        Context?.Dispose();
        return ValueTask.CompletedTask;
    }
}
