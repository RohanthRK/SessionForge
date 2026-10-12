using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Core.Storage;

namespace SessionForge.App.Views;

/// <summary>Macro editor: ordered steps (send text/key/secret, wait for pattern, sleep, call macro).</summary>
public sealed class MacroEditorWindow : DialogBase
{
    private readonly Macro _macro;
    private readonly TextBox _name = new(), _hotkey = new() { Tag = "e.g. Ctrl+Alt+1 (optional)" }, _delay = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListBox _steps = new() { Height = 260 };
    private readonly ObservableCollection<MacroStep> _items;
    private readonly TextBlock _error = new();

    private MacroEditorWindow(Macro macro)
    {
        _macro = macro;
        _items = new ObservableCollection<MacroStep>(macro.Steps);
        Title = "Macro";
        Width = 640;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "Macro", Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 12) });
        var form = new FormBuilder();
        form.Add("Name", _name).Text = macro.Name;
        form.Add("Hotkey", _hotkey).Text = macro.Hotkey ?? string.Empty;
        form.Add("Keystroke delay (ms)", _delay).Text = macro.KeystrokeDelayMs.ToString(CultureInfo.InvariantCulture);
        root.Children.Add(form.Grid);

        root.Children.Add(new TextBlock { Text = "STEPS", Style = (Style)FindResource("Text.Section") });
        var row = new DockPanel();
        var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        void Btn(string text, Action a) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6) }; b.Click += (_, _) => a(); buttons.Children.Add(b); }
        Btn("Add step…", AddStep);
        Btn("Edit…", EditStep);
        Btn("Move up", () => Move(-1));
        Btn("Move down", () => Move(1));
        Btn("Remove", () => { if (_steps.SelectedItem is MacroStep s) _items.Remove(s); });
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        _steps.ItemsSource = _items;
        _steps.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        _steps.MouseDoubleClick += (_, _) => EditStep();
        row.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(4), Child = _steps });
        root.Children.Add(row);
        root.Children.Add(new TextBlock
        {
            Text = "Text supports \\r (Enter), \\n, \\t, \\e (Esc), \\xHH and ^C-style control keys. Wait-for uses a regular expression on the terminal output (escape sequences removed). " +
                   "Send secret looks up a vault credential by name, or 'session' for the session's saved password.",
            Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 10, 0, 0),
        });
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Submit();
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
        Loaded += (_, _) => _name.Focus();
    }

    private void AddStep()
    {
        var s = MacroStepDialog.Edit(new MacroStep { Kind = MacroStepKind.SendText });
        if (s == null) return;
        int at = _steps.SelectedIndex >= 0 ? _steps.SelectedIndex + 1 : _items.Count;
        _items.Insert(at, s);
        _steps.SelectedIndex = at;
    }

    private void EditStep()
    {
        if (_steps.SelectedItem is not MacroStep s) return;
        var edited = MacroStepDialog.Edit(JsonFile.Clone(s));
        if (edited == null) return;
        int i = _items.IndexOf(s);
        _items[i] = edited;
        _steps.SelectedIndex = i;
    }

    private void Move(int d)
    {
        int i = _steps.SelectedIndex;
        int j = i + d;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        _steps.SelectedIndex = j;
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _error.Text = "Enter a name."; return; }
        if (!int.TryParse(_delay.Text, out var delay) || delay < 0 || delay > 5000) { _error.Text = "Keystroke delay must be 0–5000 ms."; return; }
        var hotkey = string.IsNullOrWhiteSpace(_hotkey.Text) ? null : _hotkey.Text.Trim();
        if (hotkey != null && !MainWindow.TryParseHotkey(hotkey, out _, out _))
        {
            _error.Text = "Hotkeys need at least one modifier, e.g. Ctrl+Alt+1 or Ctrl+Shift+F5.";
            return;
        }
        if (hotkey != null && AppServices.Macros.Value.Macros.Any(m => m.Id != _macro.Id && string.Equals(m.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase)))
        {
            _error.Text = "Another macro already uses this hotkey.";
            return;
        }
        _macro.Name = _name.Text.Trim();
        _macro.Hotkey = hotkey;
        _macro.KeystrokeDelayMs = delay;
        _macro.Steps = _items.ToList();
        DialogResult = true;
    }

    public static Macro? Edit(Macro m) => new MacroEditorWindow(m).ShowDialog() == true ? m : null;
}

public sealed class MacroStepDialog : DialogBase
{
    private readonly ComboBox _kind = new();
    private readonly TextBox _value = new() { AcceptsReturn = false, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly TextBox _timeout = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _fail = new() { Content = "Stop the macro if the pattern does not appear in time" };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly MacroStep _step;

    private MacroStepDialog(MacroStep step)
    {
        _step = step;
        Title = "Macro step";
        Width = 520;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        var form = new FormBuilder();
        _kind.ItemsSource = new[]
        {
            new Option<MacroStepKind>(MacroStepKind.SendText, "Send text"),
            new Option<MacroStepKind>(MacroStepKind.SendKey, "Send key"),
            new Option<MacroStepKind>(MacroStepKind.WaitFor, "Wait for output (regex)"),
            new Option<MacroStepKind>(MacroStepKind.Sleep, "Sleep"),
            new Option<MacroStepKind>(MacroStepKind.SendSecret, "Send secret from vault"),
            new Option<MacroStepKind>(MacroStepKind.CallMacro, "Run another macro"),
        };
        _kind.SelectedItem = ((Option<MacroStepKind>[])_kind.ItemsSource).First(o => o.Value == step.Kind);
        form.Add("Action", _kind);
        form.Add("Value", _value).Text = step.Value;
        form.Add("Timeout / duration (ms)", _timeout).Text = step.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        form.Add(string.Empty, _fail).Visibility = Visibility.Visible;
        _fail.IsChecked = step.FailOnTimeout;
        root.Children.Add(form.Grid);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Muted");
        root.Children.Add(_hint);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Submit();
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
        _kind.SelectionChanged += (_, _) => UpdateHint();
        UpdateHint();
        Loaded += (_, _) => _value.Focus();
    }

    private MacroStepKind Kind => ((Option<MacroStepKind>)_kind.SelectedItem).Value;

    private void UpdateHint()
    {
        _hint.Text = Kind switch
        {
            MacroStepKind.SendText => @"Example: ls -la\r   (\r presses Enter)",
            MacroStepKind.SendKey => "Key name, e.g. Enter, Tab, Esc, Up, F5, Ctrl+C, Alt+X, Ctrl+Up",
            MacroStepKind.WaitFor => @"Regular expression, e.g. [$#] $  or  password:\s*$",
            MacroStepKind.Sleep => "Only the duration is used.",
            MacroStepKind.SendSecret => "Name of a vault credential, or 'session' for this session's saved password. The secret is never shown or logged by the macro.",
            _ => "Exact name of another macro.",
        };
        _value.IsEnabled = Kind != MacroStepKind.Sleep;
        _fail.Visibility = Kind == MacroStepKind.WaitFor ? Visibility.Visible : Visibility.Collapsed;
        _timeout.IsEnabled = Kind is MacroStepKind.Sleep or MacroStepKind.WaitFor;
    }

    private void Submit()
    {
        if (!int.TryParse(_timeout.Text, out var t) || t < 0) { _hint.Text = "Enter a valid number of milliseconds."; return; }
        if (Kind == MacroStepKind.WaitFor)
        {
            try { _ = new System.Text.RegularExpressions.Regex(_value.Text); }
            catch (ArgumentException ex) { _hint.Text = "Invalid regular expression: " + ex.Message; return; }
        }
        _step.Kind = Kind;
        _step.Value = _value.Text;
        _step.TimeoutMs = t;
        _step.FailOnTimeout = _fail.IsChecked == true;
        DialogResult = true;
    }

    public static MacroStep? Edit(MacroStep s) => new MacroStepDialog(s).ShowDialog() == true ? s : null;
}

/// <summary>Standalone tunnel profile editor.</summary>
public sealed class TunnelEditorWindow : DialogBase
{
    private readonly TunnelProfile _p;
    private readonly TextBox _name = new();
    private readonly ComboBox _session = new() { DisplayMemberPath = nameof(Session.Name), SelectedValuePath = nameof(Session.Id) };
    private readonly CheckBox _autoStart = new() { Content = "Start automatically when SessionForge starts" };
    private readonly CheckBox _reconnect = new() { Content = "Reconnect automatically if the connection drops" };
    private readonly ListBox _list = new() { Height = 160 };
    private readonly ObservableCollection<PortForward> _forwards;
    private readonly TextBlock _error = new();

    private TunnelEditorWindow(TunnelProfile p)
    {
        _p = p;
        _forwards = new ObservableCollection<PortForward>(p.Forwards);
        Title = "Tunnel";
        Width = 560;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "SSH tunnel", Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 12) });
        var form = new FormBuilder();
        form.Add("Name", _name).Text = p.Name;
        _session.ItemsSource = AppServices.Sessions.Sessions.Where(s => s.Kind is SessionKind.Ssh or SessionKind.Sftp).OrderBy(s => s.Name).ToList();
        _session.SelectedValue = p.SessionId;
        form.Add("SSH session", _session);
        root.Children.Add(form.Grid);
        root.Children.Add(_autoStart);
        root.Children.Add(_reconnect);
        _autoStart.IsChecked = p.AutoStart;
        _reconnect.IsChecked = p.AutoReconnect;
        root.Children.Add(new TextBlock { Text = "FORWARDS", Style = (Style)FindResource("Text.Section") });
        var row = new DockPanel();
        var btns = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        void Btn(string t, Action a) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 0, 6) }; b.Click += (_, _) => a(); btns.Children.Add(b); }
        Btn("Add…", () => { var f = ForwardDialog.Edit(new PortForward { Kind = ForwardKind.Local, BindPort = 8080, TargetHost = "localhost", TargetPort = 80 }); if (f != null) _forwards.Add(f); });
        Btn("Edit…", () => { if (_list.SelectedItem is PortForward f) { int i = _forwards.IndexOf(f); var e = ForwardDialog.Edit(JsonFile.Clone(f)); if (e != null) _forwards[i] = e; } });
        Btn("Remove", () => { if (_list.SelectedItem is PortForward f) _forwards.Remove(f); });
        DockPanel.SetDock(btns, Dock.Right);
        row.Children.Add(btns);
        _list.ItemsSource = _forwards;
        row.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(4), Child = _list });
        root.Children.Add(row);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Submit();
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _error.Text = "Enter a name."; return; }
        if (_session.SelectedValue is not Guid sid) { _error.Text = "Choose the SSH session that provides the connection."; return; }
        if (_forwards.Count == 0) { _error.Text = "Add at least one forward."; return; }
        _p.Name = _name.Text.Trim();
        _p.SessionId = sid;
        _p.AutoStart = _autoStart.IsChecked == true;
        _p.AutoReconnect = _reconnect.IsChecked == true;
        _p.Forwards = _forwards.ToList();
        DialogResult = true;
    }

    public static TunnelProfile? Edit(TunnelProfile p)
    {
        if (!AppServices.Sessions.Sessions.Any(s => s.Kind is SessionKind.Ssh or SessionKind.Sftp))
        {
            MessageDialog.Info("Tunnels", "Create an SSH session first; tunnels use a saved SSH session for the connection.");
            return null;
        }
        return new TunnelEditorWindow(p).ShowDialog() == true ? p : null;
    }
}
