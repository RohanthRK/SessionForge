using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SessionForge.App.Controls;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Protocols;
using SessionForge.Protocols.Automation;
using SessionForge.Protocols.Logging;
using SessionForge.Protocols.Ssh;
using SessionForge.Terminal;

namespace SessionForge.App.Workspace;

/// <summary>Terminal session: emulator + transport + logging + macros, displayed in a <see cref="TerminalView"/>.</summary>
public sealed class TerminalSessionHost : SessionHost, IMacroTarget
{
    private readonly TerminalView _view = new();
    private readonly TerminalEmulator _emulator;
    private readonly Encoding _encoding;
    private readonly Decoder _macroDecoder;
    private ITerminalTransport? _transport;
    private SessionLogger? _logger;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _macroCts;
    private bool _userClosed;
    private int _cols = 80, _rows = 24, _pxW, _pxH;

    public TerminalSessionHost(Session session) : base(session)
    {
        var settings = AppServices.Settings;
        _encoding = ResolveEncoding(session.Terminal.Encoding);
        _macroDecoder = _encoding.GetDecoder();
        _emulator = new TerminalEmulator(80, 24, Math.Max(100, session.Terminal.ScrollbackLines > 0 ? session.Terminal.ScrollbackLines : settings.ScrollbackLines))
        {
            Encoding = _encoding,
            AllowClipboardWrite = settings.AllowOsc52Clipboard,
            Theme = ResolveTheme(session.Terminal.ThemeName ?? settings.TerminalTheme),
        };
        _emulator.Response += bytes => _transport?.Write(bytes);
        _emulator.TitleChanged += t => Ui(() => { if (!Session.Terminal.LockTitle && !string.IsNullOrWhiteSpace(t)) Title = t; });
        _emulator.Bell += () => Ui(() => _view.Terminal.Bell());
        _emulator.ClipboardWriteRequested += text => Ui(() => { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { } });
        _emulator.WorkingDirectoryChanged += dir => Ui(() => WorkingDirectoryChanged?.Invoke(dir));

        var term = _view.Terminal;
        term.Emulator = _emulator;
        term.SetFont(session.Terminal.FontFamily ?? settings.TerminalFontFamily, session.Terminal.FontSize ?? settings.TerminalFontSize);
        term.CopyOnSelect = settings.CopyOnSelect;
        term.RightClickPastes = settings.RightClickPastes;
        term.WordDelimiters = settings.WordDelimiters;
        term.BlinkCursor = settings.CursorBlink;
        term.KeyboardOptions.BackspaceSendsControlH = session.Terminal.BackspaceSendsControlH;
        term.KeywordHighlighting = session.Terminal.KeywordHighlighting ?? settings.KeywordHighlighting;
        if (session.Terminal.PasteLineDelayMs > 0) term.PasteSink = PasteWithDelay;
        term.SendInput = OnUserInput;
        term.ConfirmPaste = ConfirmPaste;
        term.OpenLink = OpenLink;
        term.ContextMenuFactory = BuildContextMenu;
        term.GridResized += (c, r, w, h) =>
        {
            (_cols, _rows, _pxW, _pxH) = (c, r, w, h);
            _transport?.Resize(c, r, w, h);
            RaiseStatusChanged();
        };
    }

    public override FrameworkElement View => _view;
    public override bool IsTerminal => true;
    public override bool SupportsSftp => Session.Kind == SessionKind.Ssh;
    public TerminalControl Terminal => _view.Terminal;
    public TerminalView TerminalView => _view;
    public TerminalEmulator Emulator => _emulator;
    public SshConnectionContext? SshContext => (_transport as SshTerminalTransport)?.Context;
    public PortForwardManager? Forwards => (_transport as SshTerminalTransport)?.Forwards;
    public string? LogPath => _logger?.Path;

    public override string StatusText =>
        $"{Session.Describe()}   •   {_cols}×{_rows}   •   {_encoding.WebName.ToUpperInvariant()}{(_logger != null ? "   •   logging" : string.Empty)}";

    /// <summary>Raised on the UI thread for every chunk the user types (used for multi-execution).</summary>
    public event Action<TerminalSessionHost, string>? UserInput;
    public event Action<string>? WorkingDirectoryChanged;
    public event Action<string>? Notice;

    /// <summary>When set, user input is suppressed locally (used while broadcasting is handled by the workspace).</summary>
    public bool MultiExecActive { get; set; }

    private static Encoding ResolveEncoding(string name)
    {
        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return new UTF8Encoding(false); }
    }

    private static TerminalTheme ResolveTheme(string name) =>
        (TerminalTheme.BuiltIn.FirstOrDefault(t => t.Name == name) ?? TerminalTheme.ForgeDark()).Clone();

    private static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    private void LocalEcho(string text, string sgr = "2")
    {
        _emulator.Feed($"\x1b[0m\x1b[{sgr}m{text.Replace("\n", "\r\n")}\x1b[0m\r\n");
    }

    #region Connection

    public override async Task ConnectAsync()
    {
        _userClosed = false;
        _view.HideOverlay();
        State = HostState.Connecting;
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        if (_transport != null) { await _transport.DisposeAsync(); _transport = null; }

        ITerminalTransport transport;
        try { transport = TransportFactory.Create(Session); }
        catch (Exception ex) { Fail(ex.Message); return; }
        _transport = transport;

        transport.Notice += msg => Ui(() =>
        {
            if (State == HostState.Connecting) LocalEcho(msg);
            Notice?.Invoke(msg);
        });
        transport.DataReceived += OnData;
        transport.Closed += reason => Ui(() => OnClosed(transport, reason));

        StartLogging();
        try
        {
            Terminal.ReportSize();
            await Task.Run(() => transport.ConnectAsync(_cols, _rows, _connectCts.Token));
            if (!ReferenceEquals(_transport, transport)) return; // superseded
            State = HostState.Connected;
            AppServices.Sessions.MarkConnected(Session.Id);
            transport.Resize(_cols, _rows, _pxW, _pxH);
            _logger?.Annotate($"Connected to {transport.DisplayName}");
            Terminal.Focus();
            RaiseStatusChanged();
            if (Session.Terminal.StartupMacroId is { } mid && AppServices.Macros.Value.Macros.FirstOrDefault(m => m.Id == mid) is { } macro)
                _ = RunMacroAsync(macro);
            if (Session.Kind == SessionKind.Ssh && Session.Ssh.RemoteMonitoring) StartMonitor();
        }
        catch (ConnectionCancelledException ex)
        {
            Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Fail("Connection cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Connect failed for '{Session.Name}': {ex}");
            Fail(ex.Message);
        }
    }

    private void Fail(string message)
    {
        State = HostState.Failed;
        LocalEcho(message, "31");
        _logger?.Annotate("Connection failed: " + message);
        _view.ShowOverlay($"Could not connect: {message}", ("Retry", () => _ = ReconnectAsync(), true), ("Edit session…", EditSession, false), ("Close", RequestClose, false));
        RaiseStatusChanged();
    }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        _emulator.Feed(data.Span);
        _logger?.Write(data.Span);
        if (_outputText != null)
        {
            var chars = new char[_macroDecoder.GetCharCount(data.Span, false)];
            int n = _macroDecoder.GetChars(data.Span, chars, false);
            _outputText.Invoke(new string(chars, 0, n));
        }
    }

    private void OnClosed(ITerminalTransport transport, string? reason)
    {
        if (!ReferenceEquals(_transport, transport)) return;
        StopMonitor();
        _logger?.Annotate(reason == null ? "Session closed" : "Session lost: " + reason);
        if (_userClosed) return;
        State = reason == null ? HostState.Disconnected : HostState.Failed;
        LocalEcho(reason == null ? "Session closed." : $"Connection lost: {reason}", reason == null ? "2" : "31");
        if (AppServices.Settings.ReconnectPrompt)
        {
            _view.ShowOverlay(reason == null ? "The session has ended." : $"Connection lost: {reason}",
                ("Reconnect", () => _ = ReconnectAsync(), true), ("Save output…", SaveOutput, false), ("Close tab", RequestClose, false));
        }
        RaiseStatusChanged();
    }

    public override async Task ReconnectAsync()
    {
        LocalEcho($"\r\n--- Reconnecting ({DateTime.Now:T}) ---");
        await ConnectAsync();
    }

    private void StartLogging()
    {
        if (_logger != null) return;
        var mode = Session.Terminal.Logging != LogMode.Off ? Session.Terminal.Logging : AppServices.Settings.DefaultLogMode;
        if (mode == LogMode.Off) return;
        try
        {
            var dir = Session.Terminal.LogDirectory ?? AppServices.Settings.LogDirectory ?? AppServices.Paths.LogDirectory;
            var path = SessionLogger.BuildPath(Environment.ExpandEnvironmentVariables(dir), AppServices.Settings.LogFileNamePattern, Session.Name);
            _logger = new SessionLogger(path, mode, _encoding);
            Notice?.Invoke($"Logging to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LocalEcho($"Could not start session log: {ex.Message}", "33");
        }
    }

    private RemoteMonitor? _monitor;

    public bool MonitorRunning => _monitor != null;

    public void StartMonitor()
    {
        if (_monitor != null || SshContext is not { } ctx) return;
        _monitor = new RemoteMonitor(ctx, TimeSpan.FromSeconds(5));
        _monitor.Updated += stats => Ui(() => { if (_monitor != null) _view.SetMonitorText(stats.Format()); });
        _monitor.Failed += msg => Ui(() => { _view.SetMonitorText(null); _monitor = null; Notice?.Invoke(msg); });
        _view.SetMonitorText("Collecting server statistics…");
        _monitor.Start();
    }

    public void StopMonitor()
    {
        _ = _monitor?.DisposeAsync();
        _monitor = null;
        _view.SetMonitorText(null);
    }

    public void ToggleLogging()
    {
        if (_logger != null)
        {
            _logger.Dispose();
            _logger = null;
            Notice?.Invoke("Logging stopped.");
        }
        else
        {
            var mode = Session.Terminal.Logging != LogMode.Off ? Session.Terminal.Logging : LogMode.PlainTextWithTimestamps;
            var dir = AppServices.Settings.LogDirectory ?? AppServices.Paths.LogDirectory;
            var path = SessionLogger.BuildPath(dir, AppServices.Settings.LogFileNamePattern, Session.Name);
            _logger = new SessionLogger(path, mode, _encoding);
            Notice?.Invoke($"Logging to {path}");
        }
        RaiseStatusChanged();
    }

    #endregion

    #region Input

    private void OnUserInput(string text)
    {
        if (MultiExecActive) { UserInput?.Invoke(this, text); return; }
        WriteToHost(text);
        UserInput?.Invoke(this, text);
    }

    /// <summary>Writes text to the host using the session encoding.</summary>
    public void WriteToHost(string text)
    {
        if (_transport == null || _transport.State != TransportState.Connected) return;
        _transport.Write(_encoding.GetBytes(text));
    }

    public override void SendText(string text) => WriteToHost(text);

    /// <summary>Sends a multi-line paste one line at a time (for devices that drop input when flooded).</summary>
    private async void PasteWithDelay(string encoded)
    {
        var lines = encoded.Split('\r');
        int delay = Math.Clamp(Session.Terminal.PasteLineDelayMs, 1, 5000);
        for (int i = 0; i < lines.Length; i++)
        {
            var chunk = i < lines.Length - 1 ? lines[i] + "\r" : lines[i];
            if (chunk.Length > 0) OnUserInput(chunk);
            if (i < lines.Length - 1) await Task.Delay(delay);
        }
    }

    private string? ConfirmPaste(string text)
    {
        var mode = AppServices.Settings.PasteWarning;
        bool multiLine = text.Contains('\n') || text.Contains('\r');
        bool suspicious = text.Any(c => c is < ' ' and not '\r' and not '\n' and not '\t');
        if (mode == PasteWarning.Never && !suspicious) return text;
        if (mode == PasteWarning.MultiLine && !multiLine && !suspicious) return text;
        var preview = text.Length > 2000 ? text[..2000] + "\n…" : text;
        var lines = text.Split('\n').Length;
        var msg = suspicious
            ? "The clipboard contains control characters that could execute commands or alter the terminal. Paste anyway?"
            : $"You are about to paste {lines} line{(lines == 1 ? "" : "s")}. Each line may execute immediately in the remote shell.";
        int choice = MessageDialog.Show("Confirm paste", msg, MessageKind.Warning, ["Paste", "Paste as one line", "Cancel"], 0, 2, preview);
        return choice switch
        {
            0 => text,
            1 => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').TrimEnd(),
            _ => null,
        };
    }

    private static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "ftp"))
        {
            if (!MessageDialog.Confirm("Open link", $"Open this {uri.Scheme} link with its default application?\n\n{url}", "Open", "Cancel")) return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { MessageDialog.Error("Open link", ex.Message); }
    }

    #endregion

    #region Macros (IMacroTarget)

    private Action<string>? _outputText;
    event Action<string>? IMacroTarget.OutputText { add => _outputText += value; remove => _outputText -= value; }
    TerminalModes IMacroTarget.Modes => _emulator.Modes;
    void IMacroTarget.SendText(string text) => Ui(() => WriteToHost(text));

    public bool MacroRunning => _macroCts != null;

    public async Task RunMacroAsync(Macro macro)
    {
        if (_macroCts != null) { Notice?.Invoke("A macro is already running in this session."); return; }
        _macroCts = new CancellationTokenSource();
        var runner = new MacroRunner(this,
            name => AppServices.Macros.Value.Macros.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)),
            ResolveMacroSecret);
        runner.Status += s => Ui(() => Notice?.Invoke(s));
        try
        {
            await Task.Run(() => runner.RunAsync(macro, _macroCts.Token));
        }
        catch (OperationCanceledException) { Notice?.Invoke($"Macro '{macro.Name}' stopped."); }
        catch (MacroException ex) { Notice?.Invoke($"Macro '{macro.Name}' failed: {ex.Message}"); }
        finally { _macroCts = null; }
    }

    public void StopMacro() => _macroCts?.Cancel();

    private string? ResolveMacroSecret(string name)
    {
        return DialogBase.OnUi(() =>
        {
            if (!UnlockVaultDialog.EnsureUnlocked("A macro needs a secret from the vault.")) return null;
            if (name.Equals("session", StringComparison.OrdinalIgnoreCase))
                return AppServices.Vault.GetSecret($"session:{Session.Id}:password");
            var cred = AppServices.Vault.Credentials.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            return cred?.Password;
        });
    }

    #endregion

    #region Commands

    public void SaveOutput()
    {
        var dlg = new SaveFileDialog { FileName = $"{Session.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt", Filter = "Text files|*.txt|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, _emulator.GetAllText(), new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageDialog.Error("Save output", ex.Message); }
    }

    public void ClearScrollback()
    {
        _emulator.Feed("\x1b[3J");
        Terminal.ScrollToBottom();
    }

    public void ResetTerminal()
    {
        _emulator.FullReset();
        Terminal.InvalidateTerminal();
    }

    private void EditSession() => EditRequested?.Invoke(Session);

    public event Action<Session>? EditRequested;
    public event Action<TerminalSessionHost>? OpenSftpRequested;
    public event Action<TerminalSessionHost>? DuplicateRequested;

    private ContextMenu BuildContextMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string header, string glyph, Action a, string? gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph }, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            return mi;
        }
        m.Items.Add(Item("Copy", "", () => Terminal.CopySelection(), "Ctrl+Shift+C", Terminal.HasSelection));
        m.Items.Add(Item("Paste", "", Terminal.Paste, "Ctrl+Shift+V"));
        m.Items.Add(Item("Select all", "", Terminal.SelectAll));
        m.Items.Add(Item("Find…", "", _view.ShowFind, "Ctrl+Shift+F"));
        m.Items.Add(new Separator());
        if (SupportsSftp) m.Items.Add(Item("Open SFTP browser", "", () => OpenSftpRequested?.Invoke(this), enabled: State == HostState.Connected));
        m.Items.Add(Item("Duplicate session", "", () => DuplicateRequested?.Invoke(this)));
        m.Items.Add(Item(State == HostState.Connected ? "Reconnect" : "Connect", "", () => _ = ReconnectAsync()));
        var macros = new MenuItem { Header = "Run macro", Icon = new TextBlock { Text = "" } };
        foreach (var macro in AppServices.Macros.Value.Macros)
        {
            var mm = macro;
            var mi = new MenuItem { Header = macro.Name };
            mi.Click += (_, _) => _ = RunMacroAsync(mm);
            macros.Items.Add(mi);
        }
        macros.IsEnabled = macros.Items.Count > 0 && State == HostState.Connected;
        m.Items.Add(macros);
        if (MacroRunning) m.Items.Add(Item("Stop macro", "", StopMacro));
        m.Items.Add(new Separator());
        if (Session.Kind == SessionKind.Ssh)
            m.Items.Add(Item(MonitorRunning ? "Hide remote monitoring" : "Show remote monitoring (Linux)", "",
                () => { if (MonitorRunning) StopMonitor(); else StartMonitor(); }, enabled: State == HostState.Connected));
        m.Items.Add(Item(_logger != null ? "Stop logging" : "Start logging", "", ToggleLogging));
        m.Items.Add(Item("Save output to file…", "", SaveOutput));
        var hl = Item("Highlight keywords", "", () => Terminal.KeywordHighlighting = !Terminal.KeywordHighlighting);
        hl.IsChecked = Terminal.KeywordHighlighting;
        m.Items.Add(hl);
        if (_transport is SessionForge.Protocols.Serial.SerialTerminalTransport serial)
            m.Items.Add(Item("Send BREAK", "", () => _ = Task.Run(() => serial.SendBreak()), enabled: State == HostState.Connected));
        m.Items.Add(Item("Clear scrollback", "", ClearScrollback));
        m.Items.Add(Item("Reset terminal", "", ResetTerminal));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Zoom in", "", () => Terminal.Zoom(1), "Ctrl++"));
        m.Items.Add(Item("Zoom out", "", () => Terminal.Zoom(-1), "Ctrl+-"));
        return m;
    }

    #endregion

    public async Task DisconnectAsync()
    {
        _userClosed = true;
        StopMonitor();
        _connectCts?.Cancel();
        _macroCts?.Cancel();
        if (_transport != null)
        {
            var t = _transport;
            _transport = null;
            await Task.Run(async () => await t.DisposeAsync());
        }
        State = HostState.Disconnected;
    }

    public override async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _logger?.Dispose();
        _logger = null;
        Terminal.Emulator = null;
    }
}
