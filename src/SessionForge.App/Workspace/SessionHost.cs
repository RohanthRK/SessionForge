using System.Windows;
using SessionForge.Core.Models;

namespace SessionForge.App.Workspace;

public enum HostState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>A live session displayed in a workspace pane (terminal, RDP, VNC, SFTP…).</summary>
public abstract class SessionHost : IAsyncDisposable
{
    private string _title;
    private HostState _state = HostState.Idle;

    protected SessionHost(Session session)
    {
        Session = session;
        _title = session.Name;
    }

    public Session Session { get; }
    public abstract FrameworkElement View { get; }
    public virtual bool IsTerminal => false;
    public virtual bool SupportsSftp => false;

    public string Title
    {
        get => _title;
        protected set { if (_title == value) return; _title = value; TitleChanged?.Invoke(); }
    }

    public HostState State
    {
        get => _state;
        protected set { if (_state == value) return; _state = value; StateChanged?.Invoke(); }
    }

    /// <summary>One-line description for the status bar.</summary>
    public virtual string StatusText => Session.Describe();

    public event Action? TitleChanged;
    public event Action? StateChanged;
    public event Action? StatusChanged;
    /// <summary>Raised when the user asks to close this host (e.g. "Close" on the disconnected overlay).</summary>
    public event Action? CloseRequested;

    protected void RaiseStatusChanged() => StatusChanged?.Invoke();
    protected void RequestClose() => CloseRequested?.Invoke();

    public abstract Task ConnectAsync();

    public virtual Task ReconnectAsync() => ConnectAsync();

    public virtual void FocusView() => View.Focus();

    /// <summary>Text typed into this terminal (for multi-execution broadcasting). Terminals only.</summary>
    public virtual void SendText(string text) { }

    public abstract ValueTask DisposeAsync();
}
