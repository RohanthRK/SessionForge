using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SessionForge.App.Services;
using SessionForge.Core.Models;

namespace SessionForge.App.Dialogs;

/// <summary>Helper to build simple label/field forms.</summary>
internal sealed class FormBuilder
{
    private readonly Grid _grid = new();

    public FormBuilder()
    {
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    public Grid Grid => _grid;

    public T Add<T>(string label, T control) where T : FrameworkElement
    {
        int r = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (label.Length > 0)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
            Grid.SetRow(l, r);
            _grid.Children.Add(l);
        }
        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, r);
        Grid.SetColumn(control, 1);
        _grid.Children.Add(control);
        return control;
    }
}

public sealed class JumpHostDialog : DialogBase
{
    private readonly TextBox _host = new(), _port = new() { Text = "22", Width = 90, HorizontalAlignment = HorizontalAlignment.Left }, _user = new(), _key = new();
    private readonly ComboBox _auth = new();
    private readonly CheckBox _save = new() { Content = "Save password in the vault when entered" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly JumpHost _jump;

    private JumpHostDialog(JumpHost jump)
    {
        _jump = jump;
        Title = "Jump host";
        Width = 460;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "SSH jump host", Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 14) });
        var f = new FormBuilder();
        f.Add("Host", _host).Text = jump.Host;
        f.Add("Port", _port).Text = jump.Port.ToString(CultureInfo.InvariantCulture);
        f.Add("Username", _user).Text = jump.Credential.Username ?? string.Empty;
        _auth.ItemsSource = new[] { AuthMethod.Password, AuthMethod.PublicKey, AuthMethod.KeyboardInteractive, AuthMethod.Agent };
        _auth.SelectedItem = jump.Auth;
        f.Add("Authentication", _auth);
        var keyRow = new DockPanel();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Private key" };
            if (d.ShowDialog() == true) _key.Text = d.FileName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        keyRow.Children.Add(browse);
        keyRow.Children.Add(_key);
        _key.Text = jump.PrivateKeyPath ?? string.Empty;
        f.Add("Key file", keyRow);
        root.Children.Add(f.Grid);
        _save.IsChecked = jump.Credential.SavePassword;
        root.Children.Add(_save);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Submit();
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
        Loaded += (_, _) => _host.Focus();
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(_host.Text)) { _error.Text = "Enter the jump host's address."; return; }
        if (!int.TryParse(_port.Text, out var p) || p is <= 0 or > 65535) { _error.Text = "Invalid port."; return; }
        _jump.Host = _host.Text.Trim();
        _jump.Port = p;
        _jump.Credential.Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
        _jump.Auth = (AuthMethod)_auth.SelectedItem;
        _jump.PrivateKeyPath = string.IsNullOrWhiteSpace(_key.Text) ? null : _key.Text.Trim();
        _jump.Credential.SavePassword = _save.IsChecked == true;
        DialogResult = true;
    }

    public static JumpHost? Edit(JumpHost j) => new JumpHostDialog(j).ShowDialog() == true ? j : null;
}

public sealed class ForwardDialog : DialogBase
{
    private readonly RadioButton _local = new() { Content = "Local  —  a port on this computer reaches a host via the SSH server" };
    private readonly RadioButton _remote = new() { Content = "Remote  —  a port on the SSH server reaches a host from this computer" };
    private readonly RadioButton _dynamic = new() { Content = "Dynamic  —  SOCKS proxy on this computer through the SSH server" };
    private readonly TextBox _name = new(), _bindAddr = new(), _bindPort = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left }, _targetHost = new(), _targetPort = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly FrameworkElement _targetHostRow, _targetPortRow;
    private readonly PortForward _f;

    private ForwardDialog(PortForward f)
    {
        _f = f;
        Title = "Port forward";
        Width = 540;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "Port forward", Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(_local);
        root.Children.Add(_remote);
        root.Children.Add(_dynamic);
        (f.Kind switch { ForwardKind.Remote => _remote, ForwardKind.Dynamic => _dynamic, _ => _local }).IsChecked = true;
        var form = new FormBuilder();
        form.Grid.Margin = new Thickness(0, 12, 0, 0);
        form.Add("Name", _name).Text = f.Name;
        form.Add("Listen address", _bindAddr).Text = f.BindAddress;
        form.Add("Listen port", _bindPort).Text = f.BindPort.ToString(CultureInfo.InvariantCulture);
        _targetHostRow = form.Add("Destination host", _targetHost);
        _targetHost.Text = f.TargetHost;
        _targetPortRow = form.Add("Destination port", _targetPort);
        _targetPort.Text = f.TargetPort.ToString(CultureInfo.InvariantCulture);
        root.Children.Add(form.Grid);
        root.Children.Add(new TextBlock { Text = "Listening on 0.0.0.0 exposes the port to your network. Remote forwards on non-loopback addresses require GatewayPorts on the server.", Style = (Style)FindResource("Text.Hint") });
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Submit();
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
        foreach (var rb in new[] { _local, _remote, _dynamic }) rb.Checked += (_, _) => UpdateRows();
        UpdateRows();
    }

    private void UpdateRows()
    {
        bool dyn = _dynamic.IsChecked == true;
        _targetHost.IsEnabled = !dyn;
        _targetPort.IsEnabled = !dyn;
    }

    private void Submit()
    {
        if (!uint.TryParse(_bindPort.Text, out var bp) || bp > 65535) { _error.Text = "Invalid listen port (use 0 for a random free port with local forwards)."; return; }
        _f.Kind = _remote.IsChecked == true ? ForwardKind.Remote : _dynamic.IsChecked == true ? ForwardKind.Dynamic : ForwardKind.Local;
        if (_f.Kind != ForwardKind.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(_targetHost.Text)) { _error.Text = "Enter the destination host."; return; }
            if (!uint.TryParse(_targetPort.Text, out var tp) || tp is 0 or > 65535) { _error.Text = "Invalid destination port."; return; }
            _f.TargetHost = _targetHost.Text.Trim();
            _f.TargetPort = tp;
        }
        if (bp == 0 && _f.Kind != ForwardKind.Local) { _error.Text = "Only local forwards may use port 0."; return; }
        _f.Name = _name.Text.Trim();
        _f.BindAddress = string.IsNullOrWhiteSpace(_bindAddr.Text) ? "127.0.0.1" : _bindAddr.Text.Trim();
        _f.BindPort = bp;
        DialogResult = true;
    }

    public static PortForward? Edit(PortForward f) => new ForwardDialog(f).ShowDialog() == true ? f : null;
}

public sealed class SessionPickerDialog : DialogBase
{
    private readonly ListBox _list = new() { Height = 300 };
    private readonly TextBox _filter = new() { Tag = "Filter…" };
    private readonly List<Session> _all;

    private SessionPickerDialog(string title, Func<Session, bool> predicate)
    {
        Title = title;
        Width = 460;
        _all = AppServices.Sessions.Sessions.Where(predicate).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_filter);
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0), Child = _list };
        root.Children.Add(card);
        _list.DisplayMemberPath = nameof(Session.Name);
        _list.ItemsSource = _all;
        _filter.TextChanged += (_, _) => _list.ItemsSource = _all.Where(s => s.Name.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase) || s.Host.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem != null) DialogResult = true; };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Select", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => { if (_list.SelectedItem != null) DialogResult = true; };
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
        Loaded += (_, _) => _filter.Focus();
    }

    public static Session? Pick(string title, Func<Session, bool> predicate)
    {
        var d = new SessionPickerDialog(title, predicate);
        return d.ShowDialog() == true ? d._list.SelectedItem as Session : null;
    }
}
