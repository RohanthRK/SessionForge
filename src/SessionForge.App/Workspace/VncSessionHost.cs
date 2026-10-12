using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Protocols;
using SessionForge.Protocols.Ssh;
using SessionForge.Protocols.Vnc;

namespace SessionForge.App.Workspace;

/// <summary>VNC viewer: renders the RFB framebuffer into a WriteableBitmap and forwards keyboard/mouse input.</summary>
public sealed class VncSessionHost : SessionHost
{
    private readonly Grid _root = new() { Focusable = true, FocusVisualStyle = null, ClipToBounds = true };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly Viewbox _box = new();
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly Border _toolbar;
    private RfbClient? _client;
    private GatewayTunnel? _tunnel;
    private WriteableBitmap? _bitmap;
    private readonly List<RfbRect> _pending = [];
    private int _updateQueued;
    private int _buttons;
    private bool _scale;

    public VncSessionHost(Session session) : base(session)
    {
        _scale = session.Vnc.Scale;
        _root.SetResourceReference(Panel.BackgroundProperty, "Bg.Window");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        bar.Children.Add(ToolButton("Ctrl+Alt+Del", "", () => _client?.SendCtrlAltDel()));
        bar.Children.Add(ToolButton("Toggle scaling", "", ToggleScale));
        bar.Children.Add(ToolButton("Send clipboard", "", SendClipboard));
        bar.Children.Add(ToolButton("Refresh", "", () => { if (_client != null) _client.RequestUpdate(false, 0, 0, _client.Width, _client.Height); }));
        var viewOnly = new CheckBox { Content = "View only", IsChecked = session.Vnc.ViewOnly, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        viewOnly.Click += (_, _) => { if (_client != null) _client.ViewOnly = viewOnly.IsChecked == true; };
        bar.Children.Add(viewOnly);
        _toolbar = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        _toolbar.SetResourceReference(Border.BackgroundProperty, "Bg.Chrome");
        _toolbar.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        _root.Children.Add(_toolbar);

        _box.Child = _image;
        var content = new Grid();
        Grid.SetRow(content, 1);
        content.Children.Add(_status);
        content.Children.Add(_scroller);
        _root.Children.Add(content);
        ApplyScale();

        _image.MouseMove += (_, e) => Pointer(e);
        _image.MouseDown += (_, e) => { _root.Focus(); _buttons |= Mask(e.ChangedButton); Pointer(e); e.Handled = true; };
        _image.MouseUp += (_, e) => { _buttons &= ~Mask(e.ChangedButton); Pointer(e); e.Handled = true; };
        _image.MouseWheel += (_, e) =>
        {
            if (_client == null) return;
            var (x, y) = ToRemote(e.GetPosition(_image));
            int wheel = e.Delta > 0 ? 8 : 16;
            _client.SendPointer(_buttons | wheel, x, y);
            _client.SendPointer(_buttons, x, y);
            e.Handled = true;
        };
        _root.PreviewKeyDown += (_, e) => Key(e, true);
        _root.PreviewKeyUp += (_, e) => Key(e, false);
        _root.TextInput += (_, e) =>
        {
            // Characters not covered by key mapping (layout-dependent) are sent as press/release pairs.
            if (_client == null || string.IsNullOrEmpty(e.Text)) return;
            foreach (var ch in e.Text)
            {
                if (ch < ' ' || _handledByKeyDown) continue;
                uint sym = ch < 256 ? ch : 0x01000000u | ch;
                _client.SendKey(sym, true);
                _client.SendKey(sym, false);
            }
            _handledByKeyDown = false;
        };
    }

    private bool _handledByKeyDown;

    public override FrameworkElement View => _root;
    public override string StatusText => _client == null ? $"VNC  {Session.Describe()}" : $"VNC  {Session.Describe()}   •   {_client.DesktopName}   •   {_client.Width}×{_client.Height}";

    private static Button ToolButton(string tip, string glyph, Action a)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon") }, ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
        b.Click += (_, _) => a();
        return b;
    }

    private void ToggleScale() { _scale = !_scale; ApplyScale(); }

    private void ApplyScale()
    {
        if (_scale)
        {
            _scroller.Content = null;
            _box.Child = null;
            _box.Child = _image;
            _scroller.Content = _box;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            _scroller.Content = null;
            _box.Child = null;
            _scroller.Content = _image;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    private void SendClipboard()
    {
        try { if (Clipboard.ContainsText()) _client?.SendClipboard(Clipboard.GetText()); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public override async Task ConnectAsync()
    {
        State = HostState.Connecting;
        _status.Text = $"Connecting to {Session.Host}…";
        _status.Visibility = Visibility.Visible;
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        _tunnel?.Dispose();
        _tunnel = null;

        string host = Session.Host;
        int port = Session.Port > 0 ? Session.Port : 5900;
        try
        {
            if (Session.Vnc.TunnelThroughSsh || Session.Ssh.JumpHosts.Count > 0)
            {
                _status.Text = "Opening SSH tunnel…";
                var connector = AppServices.CreateConnector();
                _tunnel = await Task.Run(() => GatewayTunnel.OpenAsync(connector, Session, AppServices.ResolveSession, Session.Host, port, AppServices.Settings.UseWindowsSshAgent, CancellationToken.None));
                host = "127.0.0.1";
                port = _tunnel.LocalPort;
            }

            var client = new RfbClient { ViewOnly = Session.Vnc.ViewOnly };
            client.FramebufferUpdated += rects => { lock (_pending) _pending.AddRange(rects); QueueUpdate(); };
            client.DesktopResized += (_, _) => _root.Dispatcher.BeginInvoke(CreateBitmap);
            client.ServerClipboard += text => _root.Dispatcher.BeginInvoke(() =>
            {
                if (!Session.Vnc.SharedClipboard) return;
                try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
            });
            client.Closed += reason => _root.Dispatcher.BeginInvoke(() => OnClosed(reason));

            string? Password()
            {
                if (Session.Credential.SavePassword)
                {
                    var stored = AppServices.Interaction.GetStoredSecret(new SecretTarget($"session:{Session.Id}:password", Session.Name, null));
                    if (stored != null) return stored;
                }
                var r = AppServices.Interaction.Prompt("VNC password", $"Password for {Session.Host}:", [new PromptField("Password", true)], offerSave: true);
                if (r != null && r.Save) AppServices.Interaction.StoreSecret(new SecretTarget($"session:{Session.Id}:password", Session.Name, null), r.Values[0]);
                return r?.Values[0];
            }

            await Task.Run(() => client.ConnectAsync(host, port, Password, Session.Vnc.SharedSession, CancellationToken.None));
            _client = client;
            CreateBitmap();
            _status.Visibility = Visibility.Collapsed;
            State = HostState.Connected;
            Title = string.IsNullOrWhiteSpace(client.DesktopName) ? Session.Name : $"{Session.Name} — {client.DesktopName}";
            AppServices.Sessions.MarkConnected(Session.Id);
            RaiseStatusChanged();
            _root.Focus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            State = HostState.Failed;
            _status.Text = $"Could not connect: {ex.Message}";
            _status.Visibility = Visibility.Visible;
            Log.Warn($"VNC connect failed: {ex.Message}");
        }
    }

    private void OnClosed(string? reason)
    {
        State = reason == null ? HostState.Disconnected : HostState.Failed;
        _status.Text = reason ?? "Disconnected.";
        _status.Visibility = Visibility.Visible;
        RaiseStatusChanged();
    }

    private void CreateBitmap()
    {
        if (_client == null) return;
        _bitmap = new WriteableBitmap(Math.Max(1, _client.Width), Math.Max(1, _client.Height), 96, 96, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
        _image.Width = _client.Width;
        _image.Height = _client.Height;
        lock (_pending) { _pending.Clear(); _pending.Add(new RfbRect(0, 0, _client.Width, _client.Height)); }
        QueueUpdate();
        RaiseStatusChanged();
    }

    private void QueueUpdate()
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        _root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, Blit);
    }

    private void Blit()
    {
        Interlocked.Exchange(ref _updateQueued, 0);
        var bmp = _bitmap;
        if (bmp == null || _client == null) return;
        RfbRect[] rects;
        lock (_pending) { rects = _pending.ToArray(); _pending.Clear(); }
        if (rects.Length == 0) return;
        _client.WithFramebuffer((fb, w, h) =>
        {
            if (w != bmp.PixelWidth || h != bmp.PixelHeight) return;
            bmp.Lock();
            try
            {
                foreach (var r in rects)
                {
                    int x = Math.Clamp(r.X, 0, w), y = Math.Clamp(r.Y, 0, h);
                    int rw = Math.Min(r.Width, w - x), rh = Math.Min(r.Height, h - y);
                    if (rw <= 0 || rh <= 0) continue;
                    bmp.WritePixels(new Int32Rect(x, y, rw, rh), fb, w * 4, (y * w + x) * 4);
                }
            }
            finally { bmp.Unlock(); }
        });
    }

    private (int X, int Y) ToRemote(Point p)
    {
        if (_client == null || _image.ActualWidth <= 0) return (0, 0);
        return ((int)(p.X * _client.Width / _image.ActualWidth), (int)(p.Y * _client.Height / _image.ActualHeight));
    }

    private static int Mask(MouseButton b) => b switch { MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 4, _ => 0 };

    private void Pointer(MouseEventArgs e)
    {
        if (_client == null) return;
        var (x, y) = ToRemote(e.GetPosition(_image));
        _client.SendPointer(_buttons, x, y);
    }

    private void Key(KeyEventArgs e, bool down)
    {
        if (_client == null) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        uint sym = KeySym(key);
        if (sym != 0)
        {
            _client.SendKey(sym, down);
            e.Handled = true;
            _handledByKeyDown = down;
            return;
        }
        // Letters/digits with Ctrl or Alt held: send the base character so shortcuts like Ctrl+C work remotely.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk is >= 0x41 and <= 0x5A) { _client.SendKey((uint)(vk + 0x20), down); e.Handled = true; _handledByKeyDown = down; }
            else if (vk is >= 0x30 and <= 0x39) { _client.SendKey((uint)vk, down); e.Handled = true; _handledByKeyDown = down; }
        }
    }

    /// <summary>X11 keysyms for non-character keys.</summary>
    private static uint KeySym(Key k) => k switch
    {
        System.Windows.Input.Key.Back => 0xFF08, System.Windows.Input.Key.Tab => 0xFF09, System.Windows.Input.Key.Enter => 0xFF0D, System.Windows.Input.Key.Escape => 0xFF1B,
        System.Windows.Input.Key.Insert => 0xFF63, System.Windows.Input.Key.Delete => 0xFFFF, System.Windows.Input.Key.Home => 0xFF50, System.Windows.Input.Key.End => 0xFF57,
        System.Windows.Input.Key.PageUp => 0xFF55, System.Windows.Input.Key.PageDown => 0xFF56,
        System.Windows.Input.Key.Left => 0xFF51, System.Windows.Input.Key.Up => 0xFF52, System.Windows.Input.Key.Right => 0xFF53, System.Windows.Input.Key.Down => 0xFF54,
        >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F12 => 0xFFBE + (uint)(k - System.Windows.Input.Key.F1),
        System.Windows.Input.Key.LeftShift => 0xFFE1, System.Windows.Input.Key.RightShift => 0xFFE2,
        System.Windows.Input.Key.LeftCtrl => 0xFFE3, System.Windows.Input.Key.RightCtrl => 0xFFE4,
        System.Windows.Input.Key.LeftAlt => 0xFFE9, System.Windows.Input.Key.RightAlt => 0xFFEA,
        System.Windows.Input.Key.LWin => 0xFFEB, System.Windows.Input.Key.RWin => 0xFFEC,
        System.Windows.Input.Key.CapsLock => 0xFFE5,
        _ => 0,
    };

    public override void FocusView() => _root.Focus();

    public override async ValueTask DisposeAsync()
    {
        if (_client != null) await _client.DisposeAsync();
        _client = null;
        _tunnel?.Dispose();
        _tunnel = null;
    }
}
