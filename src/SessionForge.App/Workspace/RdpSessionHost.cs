using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Protocols;
using SessionForge.Protocols.Ssh;
using WinForms = System.Windows.Forms;

namespace SessionForge.App.Workspace;

/// <summary>
/// Remote Desktop session using the Windows RDP ActiveX control (mstscax.dll, MsRdpClient*NotSafeForScripting).
/// Property and event names were verified against the type library installed on Windows.
/// </summary>
public sealed class RdpSessionHost : SessionHost
{
    private static readonly string[] ProgIds = ["MsTscAx.MsTscAx.13", "MsTscAx.MsTscAx.12", "MsTscAx.MsTscAx.11", "MsTscAx.MsTscAx.10", "MsTscAx.MsTscAx.9"];

    private readonly Grid _root = new();
    private readonly WindowsFormsHost _wfHost = new();
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40) };
    private readonly StackPanel _statusPanel = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private RdpAxHost? _ax;
    private GatewayTunnel? _tunnel;
    private readonly System.Windows.Threading.DispatcherTimer _resizeTimer;
    private bool _userDisconnect;

    public RdpSessionHost(Session session) : base(session)
    {
        _root.SetResourceReference(Panel.BackgroundProperty, "Bg.Window");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        _statusPanel.Children.Add(_status);
        _root.Children.Add(_statusPanel);
        _root.Children.Add(_wfHost);
        _wfHost.Visibility = Visibility.Hidden;
        _resizeTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(700), System.Windows.Threading.DispatcherPriority.Background, (_, _) => ApplyResize(), _root.Dispatcher);
        _root.SizeChanged += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
    }

    public override FrameworkElement View => _root;
    public override string StatusText => $"RDP  {Session.Describe()}{(_tunnel != null ? "   •   via SSH " + _tunnel.Description : string.Empty)}";

    private void SetStatus(string text, bool showRetry = false)
    {
        _status.Text = text;
        while (_statusPanel.Children.Count > 1) _statusPanel.Children.RemoveAt(1);
        if (showRetry)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            var retry = new Button { Content = "Reconnect", Style = (Style)Application.Current.FindResource("Button.Primary"), Margin = new Thickness(4) };
            retry.Click += (_, _) => _ = ReconnectAsync();
            var close = new Button { Content = "Close", Margin = new Thickness(4) };
            close.Click += (_, _) => RequestClose();
            bar.Children.Add(retry);
            bar.Children.Add(close);
            _statusPanel.Children.Add(bar);
        }
        _wfHost.Visibility = Visibility.Hidden;
        _statusPanel.Visibility = Visibility.Visible;
    }

    public override async Task ConnectAsync()
    {
        _userDisconnect = false;
        State = HostState.Connecting;
        SetStatus($"Connecting to {Session.Host}…");
        DisposeControl();

        string host = Session.Host;
        int port = Session.Port > 0 ? Session.Port : 3389;
        try
        {
            if (Session.Rdp.TunnelThroughSsh || Session.Ssh.JumpHosts.Count > 0)
            {
                SetStatus($"Opening SSH tunnel to {Session.Host}:{port}…");
                var connector = AppServices.CreateConnector();
                _tunnel = await Task.Run(() => GatewayTunnel.OpenAsync(connector, Session, AppServices.ResolveSession, Session.Host, port, AppServices.Settings.UseWindowsSshAgent, CancellationToken.None));
                host = "127.0.0.1";
                port = _tunnel.LocalPort;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            State = HostState.Failed;
            SetStatus("SSH tunnel failed: " + ex.Message, showRetry: true);
            return;
        }

        string? user = Session.Credential.Username, domain = Session.Rdp.Domain, password = null;
        if (Session.Credential.CredentialId is { } cid && AppServices.Interaction.GetCredential(cid) is { } cred)
        {
            user = cred.Username ?? user;
            password = cred.Password;
        }
        else if (Session.Credential.SavePassword)
        {
            password = AppServices.Interaction.GetStoredSecret(new SecretTarget($"session:{Session.Id}:password", Session.Name, user));
        }
        if (user != null && user.Contains('\\') && string.IsNullOrEmpty(domain))
        {
            var parts = user.Split('\\', 2);
            domain = parts[0];
            user = parts[1];
        }

        try
        {
            CreateControl();
            await WaitForHandleAsync();
            dynamic ocx = _ax!.Ocx ?? throw new InvalidOperationException("The Remote Desktop control did not initialise.");
            ocx.Server = host;
            if (!string.IsNullOrEmpty(user)) ocx.UserName = user;
            if (!string.IsNullOrEmpty(domain)) ocx.Domain = domain;
            var (w, h) = DesktopSize();
            ocx.DesktopWidth = w;
            ocx.DesktopHeight = h;
            ocx.ColorDepth = Session.Rdp.ColorDepth is 15 or 16 or 24 or 32 ? Session.Rdp.ColorDepth : 32;
            ocx.ConnectingText = $"Connecting to {Session.Host}…";

            dynamic adv = ocx.AdvancedSettings9;
            adv.RDPPort = port;
            adv.EnableCredSspSupport = Session.Rdp.EnableCredSsp;
            adv.AuthenticationLevel = (uint)Math.Clamp(Session.Rdp.AuthenticationLevel, 0, 2);
            adv.SmartSizing = Session.Rdp.SmartSizing;
            adv.Compress = Session.Rdp.Compression ? 1 : 0;
            adv.RedirectClipboard = Session.Rdp.RedirectClipboard;
            adv.RedirectDrives = Session.Rdp.RedirectDrives;
            adv.RedirectPrinters = Session.Rdp.RedirectPrinters;
            adv.RedirectSmartCards = Session.Rdp.RedirectSmartCards;
            adv.RedirectPorts = Session.Rdp.RedirectPorts;
            adv.AudioRedirectionMode = (uint)Math.Clamp(Session.Rdp.AudioMode, 0, 2);
            adv.AudioCaptureRedirectionMode = Session.Rdp.RedirectMicrophone;
            adv.ConnectToAdministerServer = Session.Rdp.AdminConsole;
            adv.EnableAutoReconnect = true;
            adv.MaxReconnectAttempts = 5;
            adv.GrabFocusOnConnect = true;
            adv.ContainerHandledFullScreen = 1;
            adv.BitmapPersistence = 1;
            if (!string.IsNullOrEmpty(password)) adv.ClearTextPassword = password;

            if (!string.IsNullOrWhiteSpace(Session.Rdp.Gateway))
            {
                dynamic ts = ocx.TransportSettings4;
                ts.GatewayHostname = Session.Rdp.Gateway;
                ts.GatewayUsageMethod = 1u;
                ts.GatewayProfileUsageMethod = 1u;
                ts.GatewayCredsSource = 0u;
            }
            dynamic secured = ocx.SecuredSettings3;
            secured.KeyboardHookMode = 1; // forward Windows shortcuts to the remote session when it has focus

            _wfHost.Visibility = Visibility.Visible;
            _statusPanel.Visibility = Visibility.Collapsed;
            ocx.Connect();
        }
        catch (COMException ex)
        {
            Log.Error("RDP setup failed", ex);
            State = HostState.Failed;
            SetStatus($"Remote Desktop client error: {ex.Message}", showRetry: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Error("RDP setup failed", ex);
            State = HostState.Failed;
            SetStatus($"Remote Desktop client error: {ex.Message}", showRetry: true);
        }
    }

    private (int W, int H) DesktopSize()
    {
        if (Session.Rdp.Width > 0 && Session.Rdp.Height > 0) return (Session.Rdp.Width, Session.Rdp.Height);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_root);
        int w = (int)Math.Max(800, _root.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Max(600, _root.ActualHeight * dpi.DpiScaleY);
        // RDP requires an even width.
        return (w & ~1, h);
    }

    private void CreateControl()
    {
        Exception? last = null;
        foreach (var progId in ProgIds)
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type == null) continue;
            try
            {
                var ax = new RdpAxHost(type.GUID.ToString());
                ax.Dock = WinForms.DockStyle.Fill;
                ax.Connected += () => _root.Dispatcher.BeginInvoke(() => { State = HostState.Connected; AppServices.Sessions.MarkConnected(Session.Id); RaiseStatusChanged(); });
                ax.LoginComplete += () => _root.Dispatcher.BeginInvoke(() => Title = Session.Name);
                ax.Disconnected += reason => _root.Dispatcher.BeginInvoke(() => OnDisconnected(reason));
                ax.FatalError += code => _root.Dispatcher.BeginInvoke(() => { State = HostState.Failed; SetStatus($"Fatal RDP error {code}.", true); });
                _wfHost.Child = ax;
                _ax = ax;
                return;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { last = ex; }
        }
        throw new InvalidOperationException("The Windows Remote Desktop ActiveX control (mstscax.dll) is not available.", last);
    }

    private async Task WaitForHandleAsync()
    {
        for (int i = 0; i < 100 && (_ax == null || !_ax.Created); i++)
        {
            if (_ax is { IsHandleCreated: false } && _root.IsLoaded) _ax.CreateControl();
            await Task.Delay(20);
        }
        if (_ax == null || !_ax.Created) throw new InvalidOperationException("The Remote Desktop control could not be created.");
    }

    private void OnDisconnected(int reason)
    {
        string text = DescribeReason(reason);
        _tunnel?.Dispose();
        _tunnel = null;
        if (_userDisconnect) { State = HostState.Disconnected; return; }
        State = reason is 1 or 2 or 3 ? HostState.Disconnected : HostState.Failed;
        SetStatus(text, showRetry: true);
        RaiseStatusChanged();
    }

    private string DescribeReason(int reason)
    {
        try
        {
            if (_ax?.Ocx is { } o)
            {
                dynamic ocx = o;
                string desc = ocx.GetErrorDescription((uint)reason, (uint)ocx.ExtendedDisconnectReason);
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return reason switch
        {
            1 => "Disconnected.",
            2 => "The session was disconnected by the remote side (logoff or another user connected).",
            3 => "The server disconnected the session.",
            260 or 516 or 520 => "The remote computer could not be reached.",
            264 => "Connection timed out.",
            2055 => "Logon failed: the credentials were not accepted.",
            _ => $"Disconnected (code {reason}).",
        };
    }

    private void ApplyResize()
    {
        _resizeTimer.Stop();
        if (_ax?.Ocx == null || State != HostState.Connected || Session.Rdp.Width > 0) return;
        try
        {
            dynamic ocx = _ax.Ocx;
            var (w, h) = DesktopSize();
            // Dynamic resolution (Windows 8.1 / Server 2012 R2 and newer servers); silently ignored otherwise.
            ocx.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)w, (uint)h, 0u, 100u, 100u);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException) { }
    }

    private void DisposeControl()
    {
        if (_ax == null) return;
        try
        {
            dynamic ocx = _ax.Ocx!;
            if ((int)ocx.Connected != 0) ocx.Disconnect();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NullReferenceException) { }
        _wfHost.Child = null;
        _ax.Dispose();
        _ax = null;
    }

    public override void FocusView()
    {
        _ax?.Focus();
    }

    public override ValueTask DisposeAsync()
    {
        _userDisconnect = true;
        _resizeTimer.Stop();
        DisposeControl();
        _tunnel?.Dispose();
        _tunnel = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>AxHost wrapper with an IMsTscAxEvents sink.</summary>
internal sealed class RdpAxHost : WinForms.AxHost
{
    private ConnectionPointCookie? _cookie;

    public RdpAxHost(string clsid) : base(clsid) { }

    public object? Ocx => GetOcx();

    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action<int>? FatalError;

    internal void RaiseConnected() => Connected?.Invoke();
    internal void RaiseLoginComplete() => LoginComplete?.Invoke();
    internal void RaiseDisconnected(int r) => Disconnected?.Invoke(r);
    internal void RaiseFatal(int c) => FatalError?.Invoke(c);

    protected override void CreateSink()
    {
        try { _cookie = new ConnectionPointCookie(GetOcx()!, new RdpEventSink(this), typeof(IMsTscAxEvents)); }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException or COMException) { Log.Warn("RDP event sink failed: " + ex.Message); }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }
}

/// <summary>IMsTscAxEvents dispinterface; DISPIDs verified from mstscax.dll's type library.</summary>
[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(22)] void OnLogonError(int lError);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IMsTscAxEvents
{
    private readonly RdpAxHost _host;
    internal RdpEventSink(RdpAxHost host) => _host = host;
    public void OnConnecting() { }
    public void OnConnected() => _host.RaiseConnected();
    public void OnLoginComplete() => _host.RaiseLoginComplete();
    public void OnDisconnected(int discReason) => _host.RaiseDisconnected(discReason);
    public void OnFatalError(int errorCode) => _host.RaiseFatal(errorCode);
    public void OnWarning(int warningCode) => Log.Warn($"RDP warning {warningCode}");
    public void OnRemoteDesktopSizeChange(int width, int height) { }
    public void OnLogonError(int lError) => Log.Warn($"RDP logon error {lError}");
}
