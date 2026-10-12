using System.IO.Ports;
using SessionForge.Core.Models;

namespace SessionForge.Protocols.Serial;

/// <summary>Serial (COM) port terminal.</summary>
public sealed class SerialTerminalTransport : ITerminalTransport
{
    private readonly SerialOptions _o;
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private int _closed;
    private readonly object _writeLock = new();

    public SerialTerminalTransport(SerialOptions options)
    {
        _o = options;
        DisplayName = $"{options.PortName} {options.BaudRate} {options.DataBits}{ParityChar(options.Parity)}{StopBitsText(options.StopBits)}";
    }

    public static string[] AvailablePorts() => SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    private static char ParityChar(SerialParity p) => p switch { SerialParity.Odd => 'O', SerialParity.Even => 'E', SerialParity.Mark => 'M', SerialParity.Space => 'S', _ => 'N' };
    private static string StopBitsText(SerialStopBits s) => s switch { SerialStopBits.OnePointFive => "1.5", SerialStopBits.Two => "2", _ => "1" };

    public string DisplayName { get; }
    public TransportState State { get; private set; }
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string?>? Closed;
    public event Action<string>? Notice;

    public Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        State = TransportState.Connecting;
        _port = new SerialPort(_o.PortName, _o.BaudRate,
            _o.Parity switch { SerialParity.Odd => Parity.Odd, SerialParity.Even => Parity.Even, SerialParity.Mark => Parity.Mark, SerialParity.Space => Parity.Space, _ => Parity.None },
            Math.Clamp(_o.DataBits, 5, 8),
            _o.StopBits switch { SerialStopBits.OnePointFive => StopBits.OnePointFive, SerialStopBits.Two => StopBits.Two, _ => StopBits.One })
        {
            Handshake = _o.FlowControl switch
            {
                SerialFlowControl.XonXoff => Handshake.XOnXOff,
                SerialFlowControl.RtsCts => Handshake.RequestToSend,
                _ => Handshake.None,
            },
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
            ReadBufferSize = 64 * 1024,
        };
        try
        {
            _port.Open();
            // DSR/DTR flow control isn't a native Handshake mode; assert DTR and let the device gate on it.
            _port.DtrEnable = true;
            if (_o.FlowControl != SerialFlowControl.RtsCts) _port.RtsEnable = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            State = TransportState.Failed;
            throw new IOException($"Cannot open {_o.PortName}: {ex.Message}", ex);
        }
        State = TransportState.Connected;
        Notice?.Invoke($"Opened {DisplayName}");
        _cts = new CancellationTokenSource();
        var stream = _port.BaseStream;
        _ = Task.Run(async () =>
        {
            var buf = new byte[8192];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    DataReceived?.Invoke(buf.AsMemory(0, n));
                }
                Close(null);
            }
            catch (OperationCanceledException) { Close(null); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Close($"Serial port error: {ex.Message}");
            }
        });
        return Task.CompletedTask;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (State != TransportState.Connected || _port == null) return;
        byte[] payload = data.ToArray();
        if (_o.SendCrLf)
        {
            var list = new List<byte>(payload.Length + 4);
            for (int i = 0; i < payload.Length; i++)
            {
                list.Add(payload[i]);
                if (payload[i] == '\r' && (i + 1 >= payload.Length || payload[i + 1] != '\n')) list.Add((byte)'\n');
            }
            payload = list.ToArray();
        }
        try
        {
            lock (_writeLock) _port.BaseStream.Write(payload);
            if (_o.LocalEcho) DataReceived?.Invoke(payload);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Close($"Serial write failed: {ex.Message}");
        }
    }

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) { }

    /// <summary>Sends a serial BREAK for the given duration.</summary>
    public void SendBreak(int milliseconds = 250)
    {
        if (_port is not { IsOpen: true }) return;
        _port.BreakState = true;
        Thread.Sleep(milliseconds);
        _port.BreakState = false;
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
        try { _port?.Close(); } catch (IOException) { }
        _port?.Dispose();
        State = TransportState.Closed;
        return ValueTask.CompletedTask;
    }
}
