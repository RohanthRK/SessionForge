using System.Net.Sockets;
using SessionForge.Core.Models;

namespace SessionForge.Protocols.Telnet;

/// <summary>Telnet or raw TCP terminal connection.</summary>
public sealed class TcpTerminalTransport : ITerminalTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly TelnetProtocol? _telnet;
    private readonly bool _returnSendsNewline;
    private readonly int _timeoutSeconds;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private int _closed;
    private readonly object _writeLock = new();

    public TcpTerminalTransport(Session session, bool telnet)
    {
        _host = session.Host;
        _port = session.Port > 0 ? session.Port : (telnet ? 23 : 0);
        _telnet = telnet ? new TelnetProtocol(session.Telnet.TerminalType) : null;
        _returnSendsNewline = session.Telnet.ReturnSendsNewline;
        _timeoutSeconds = Math.Max(3, session.Ssh.ConnectTimeoutSeconds);
        DisplayName = $"{(telnet ? "telnet" : "raw")}://{_host}:{_port}";
    }

    public string DisplayName { get; }
    public TransportState State { get; private set; }
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string?>? Closed;
    public event Action<string>? Notice;

    public async Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        State = TransportState.Connecting;
        Notice?.Invoke($"Connecting to {_host}:{_port}…");
        _tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                await _tcp.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                State = TransportState.Failed;
                throw new TimeoutException($"Timed out connecting to {_host}:{_port}.");
            }
            catch (SocketException ex)
            {
                State = TransportState.Failed;
                throw new IOException($"Could not connect to {_host}:{_port} — {ex.Message}", ex);
            }
        }
        _stream = _tcp.GetStream();
        State = TransportState.Connected;
        if (_telnet != null)
        {
            _telnet.SetWindowSize(columns, rows);
            RawWrite(_telnet.InitialNegotiation());
        }
        Notice?.Invoke($"Connected to {_host}:{_port}");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var replies = new List<byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;
                if (_telnet != null)
                {
                    replies.Clear();
                    var data = _telnet.Receive(buf.AsSpan(0, n), replies);
                    if (replies.Count > 0) RawWrite(replies.ToArray());
                    if (data.Length > 0) DataReceived?.Invoke(data);
                }
                else DataReceived?.Invoke(buf.AsMemory(0, n));
            }
            Close(null);
        }
        catch (OperationCanceledException) { Close(null); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { Close(ex.Message); }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (State != TransportState.Connected) return;
        RawWrite(_telnet != null ? _telnet.EscapeOutgoing(data, _returnSendsNewline) : data);
    }

    private void RawWrite(ReadOnlySpan<byte> data)
    {
        try { lock (_writeLock) _stream?.Write(data); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { Close(ex.Message); }
    }

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        if (_telnet == null) return;
        var msg = _telnet.SetWindowSize(columns, rows);
        if (msg.Length > 0) RawWrite(msg);
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
        _cts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        State = TransportState.Closed;
        return ValueTask.CompletedTask;
    }
}
