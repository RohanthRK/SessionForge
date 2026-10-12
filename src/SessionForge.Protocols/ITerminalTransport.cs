namespace SessionForge.Protocols;

public enum TransportState { Disconnected, Connecting, Connected, Closed, Failed }

/// <summary>
/// A bidirectional byte stream feeding a terminal: SSH shell, Telnet, serial port, local ConPTY, raw TCP.
/// Implementations raise <see cref="DataReceived"/> from a background thread.
/// </summary>
public interface ITerminalTransport : IAsyncDisposable
{
    string DisplayName { get; }
    TransportState State { get; }

    /// <summary>Raised with chunks of host output. The memory is only valid for the duration of the call.</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>Raised once when the connection ends. The argument is an error message, or null for a clean close.</summary>
    event Action<string?>? Closed;

    /// <summary>Informational messages for the user (banners, forwarding status, warnings).</summary>
    event Action<string>? Notice;

    Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken);

    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows, int pixelWidth, int pixelHeight);
}

/// <summary>Raised in place of a generic exception when the user cancels an interactive prompt during connect.</summary>
public sealed class ConnectionCancelledException : OperationCanceledException
{
    public ConnectionCancelledException(string message) : base(message) { }
}
