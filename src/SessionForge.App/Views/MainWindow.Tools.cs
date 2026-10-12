using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SessionForge.App.Controls;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.App.Workspace;
using SessionForge.Core.Automation;
using SessionForge.Core.Models;
using SessionForge.Core.Storage;

namespace SessionForge.App.Views;

public partial class MainWindow
{
    private readonly ObservableCollection<TunnelRuntime> _tunnels = [];
    private Macro? _recording;
    private DateTime _lastRecordedInput;

    #region Sidebar

    private bool _railUpdating;

    private ToggleButton[] RailButtons => [RailSessions, RailSftp, RailMacros, RailTunnels];

    /// <summary>
    /// Rail buttons react to IsChecked changes (not Click) so keyboard, screen readers and UI automation behave like the mouse:
    /// checking one shows its panel exclusively; unchecking the active one collapses the sidebar.
    /// </summary>
    private void Rail_Checked(object sender, RoutedEventArgs e)
    {
        if (_railUpdating) return;
        _railUpdating = true;
        try
        {
            foreach (var b in RailButtons) if (b != sender) b.IsChecked = false;
            ShowSidebarColumn(true);
            ApplySidebarVisibility();
        }
        finally { _railUpdating = false; }
    }

    private void Rail_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_railUpdating) return;
        if (RailButtons.All(b => b.IsChecked != true)) ShowSidebarColumn(false);
    }

    private void ApplySidebarVisibility()
    {
        SessionsPanel.Visibility = RailSessions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = RailSftp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MacrosPanel.Visibility = RailMacros.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TunnelsPanel.Visibility = RailTunnels.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (RailSftp.IsChecked == true) UpdateSftpPanel();
    }

    private double _sidebarWidth = 270;

    /// <summary>Shows/hides the sidebar, keeping the rail selection consistent.</summary>
    private void SetSidebarVisible(bool visible)
    {
        if (visible)
        {
            if (RailButtons.All(b => b.IsChecked != true)) RailSessions.IsChecked = true; // raises Rail_Checked
            else ShowSidebarColumn(true);
        }
        else
        {
            _railUpdating = true;
            try { foreach (var b in RailButtons) b.IsChecked = false; }
            finally { _railUpdating = false; }
            ShowSidebarColumn(false);
        }
    }

    /// <summary>Opens a specific rail panel.</summary>
    private void ShowPanel(ToggleButton rail)
    {
        if (rail.IsChecked == true) { ShowSidebarColumn(true); ApplySidebarVisibility(); }
        else rail.IsChecked = true;
    }

    private void ShowSidebarColumn(bool visible)
    {
        bool currentlyVisible = SidebarColumn.Width.Value > 0;
        if (visible && !currentlyVisible)
        {
            SidebarColumn.Width = new GridLength(Math.Max(160, _sidebarWidth));
            SplitterColumn.Width = new GridLength(4);
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else if (!visible && currentlyVisible)
        {
            _sidebarWidth = SidebarColumn.Width.Value;
            SidebarColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
        if (AppServices.Settings.ShowSidebar != visible) AppServices.SettingsStore.Update(s => s.ShowSidebar = visible);
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _sidebarWidth = SidebarColumn.ActualWidth;
        AppServices.SettingsStore.Update(s => s.SidebarWidth = _sidebarWidth);
    }

    #endregion

    #region SFTP side panel

    private void EnsureSftpBrowser(TerminalSessionHost host, bool activate)
    {
        if (host.SshContext == null) return;
        if (!_sftpBrowsers.ContainsKey(host))
        {
            var browser = new SftpBrowser();
            browser.Notice += m => ShowStatusNotice(m);
            _sftpBrowsers[host] = browser;
            _ = browser.ConnectAsync(host.SshContext, host.Session.Ssh.StartupDirectory);
        }
        if (activate) ShowPanel(RailSftp);
        UpdateSftpPanel();
    }

    private void UpdateSftpPanel()
    {
        foreach (var b in _sftpBrowsers.Values) b.Visibility = Visibility.Collapsed;
        if (_current?.ActiveHost is TerminalSessionHost th && th.SupportsSftp)
        {
            SftpPanelTitle.Text = "SFTP — " + th.Session.Name;
            if (_sftpBrowsers.TryGetValue(th, out var browser))
            {
                if (!SftpPanelHost.Children.Contains(browser)) SftpPanelHost.Children.Add(browser);
                browser.Visibility = Visibility.Visible;
                SftpPanelHint.Visibility = Visibility.Collapsed;
                return;
            }
            if (th.State == HostState.Connected && RailSftp.IsChecked == true)
            {
                EnsureSftpBrowser(th, activate: false);
                return;
            }
            SftpPanelHint.Text = th.State == HostState.Connected ? "Opening SFTP…" : "Waiting for the SSH connection…";
        }
        else
        {
            SftpPanelTitle.Text = "SFTP";
            SftpPanelHint.Text = "Select a connected SSH tab to browse its files. Drag files from Explorer onto the list to upload.";
        }
        SftpPanelHint.Visibility = Visibility.Visible;
    }

    #endregion

    #region Macros

    private void RefreshMacros()
    {
        MacroList.ItemsSource = AppServices.Macros.Value.Macros.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private TerminalSessionHost? ActiveTerminal => _current?.ActiveHost as TerminalSessionHost;

    private void MacroList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RunMacro_Click(sender, e);

    private void RunMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m) return;
        if (ActiveTerminal is not { State: HostState.Connected } t) { ShowToast("Open and connect a terminal first."); return; }
        _ = t.RunMacroAsync(m);
        t.Terminal.Focus();
    }

    private void RunMacroAll_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m || _current == null) return;
        foreach (var l in _current.Leaves)
            if (l.Host is TerminalSessionHost { State: HostState.Connected } t) _ = t.RunMacroAsync(m);
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        var m = MacroEditorWindow.Edit(new Macro());
        if (m != null) AppServices.Macros.Update(d => d.Macros.Add(m));
    }

    private void EditMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m) return;
        var edited = MacroEditorWindow.Edit(JsonFile.Clone(m));
        if (edited == null) return;
        AppServices.Macros.Update(d => { int i = d.Macros.FindIndex(x => x.Id == m.Id); if (i >= 0) d.Macros[i] = edited; });
    }

    private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m) return;
        var copy = JsonFile.Clone(m);
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        copy.Hotkey = null;
        AppServices.Macros.Update(d => d.Macros.Add(copy));
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m) return;
        if (!MessageDialog.Confirm("Delete macro", $"Delete '{m.Name}'?", "Delete", "Cancel", MessageKind.Warning)) return;
        AppServices.Macros.Update(d => d.Macros.RemoveAll(x => x.Id == m.Id));
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (RecordToggle.IsChecked == true)
        {
            if (ActiveTerminal == null) { RecordToggle.IsChecked = false; ShowToast("Select a terminal tab to record from."); return; }
            _recording = new Macro { Name = $"Recorded {DateTime.Now:g}" };
            _lastRecordedInput = DateTime.UtcNow;
            ShowToast("Recording: everything you type in the active terminal is captured. Click the record button again to stop.");
            ActiveTerminal.Terminal.Focus();
        }
        else if (_recording != null)
        {
            var m = _recording;
            _recording = null;
            if (m.Steps.Count == 0) { ShowToast("Nothing was recorded."); return; }
            var edited = MacroEditorWindow.Edit(m);
            if (edited != null) AppServices.Macros.Update(d => d.Macros.Add(edited));
        }
    }

    /// <summary>Runs the macro whose hotkey (e.g. "Ctrl+Alt+1") matches, in the active terminal.</summary>
    private bool TryRunMacroHotkey(Key key, ModifierKeys mods)
    {
        if (ActiveTerminal is not { State: HostState.Connected } t) return false;
        foreach (var macro in AppServices.Macros.Value.Macros)
        {
            if (string.IsNullOrWhiteSpace(macro.Hotkey) || !TryParseHotkey(macro.Hotkey, out var hk, out var hm)) continue;
            if (hk == key && hm == mods)
            {
                _ = t.RunMacroAsync(macro);
                return true;
            }
        }
        return false;
    }

    public static bool TryParseHotkey(string text, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        var last = parts[^1];
        if (last.Length == 1 && char.IsDigit(last[0])) last = "D" + last;
        return Enum.TryParse(last, ignoreCase: true, out key) && key != Key.None && mods != ModifierKeys.None;
    }

    private void RecordInput(string text)
    {
        var m = _recording!;
        var now = DateTime.UtcNow;
        var gap = now - _lastRecordedInput;
        _lastRecordedInput = now;
        // Long pauses become Sleep steps (approximate timing, capped).
        if (m.Steps.Count > 0 && gap > TimeSpan.FromSeconds(1.5))
            m.Steps.Add(new MacroStep { Kind = MacroStepKind.Sleep, TimeoutMs = (int)Math.Min(gap.TotalMilliseconds, 10_000) });
        if (m.Steps.Count > 0 && m.Steps[^1].Kind == MacroStepKind.SendText)
            m.Steps[^1].Value += MacroText.Escape(text);
        else
            m.Steps.Add(new MacroStep { Kind = MacroStepKind.SendText, Value = MacroText.Escape(text) });
    }

    #endregion

    #region Tunnels

    private void RefreshTunnels()
    {
        var profiles = AppServices.Tunnels.Value.Tunnels;
        foreach (var rt in _tunnels.Where(t => profiles.All(p => p.Id != t.Profile.Id)).ToList()) { rt.Stop(); _tunnels.Remove(rt); }
        foreach (var p in profiles)
        {
            var rt = _tunnels.FirstOrDefault(t => t.Profile.Id == p.Id);
            if (rt == null) _tunnels.Add(new TunnelRuntime(p));
            else rt.Update(p);
        }
        TunnelList.ItemsSource = _tunnels;
    }

    private void StartAutoTunnels()
    {
        foreach (var t in _tunnels.Where(t => t.Profile.AutoStart)) t.Start();
    }

    private void StopAllTunnels()
    {
        foreach (var t in _tunnels) t.Stop();
    }

    private void NewTunnel_Click(object sender, RoutedEventArgs e)
    {
        var p = TunnelEditorWindow.Edit(new TunnelProfile());
        if (p != null) AppServices.Tunnels.Update(d => d.Tunnels.Add(p));
    }

    private void EditTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelRuntime rt) return;
        var edited = TunnelEditorWindow.Edit(JsonFile.Clone(rt.Profile));
        if (edited == null) return;
        bool wasActive = rt.IsActive;
        if (wasActive) rt.Stop();
        AppServices.Tunnels.Update(d => { int i = d.Tunnels.FindIndex(x => x.Id == edited.Id); if (i >= 0) d.Tunnels[i] = edited; });
        if (wasActive) _tunnels.FirstOrDefault(t => t.Profile.Id == edited.Id)?.Start();
    }

    private void DeleteTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelRuntime rt) return;
        if (!MessageDialog.Confirm("Delete tunnel", $"Delete '{rt.Name}'?", "Delete", "Cancel", MessageKind.Warning)) return;
        rt.Stop();
        AppServices.Tunnels.Update(d => d.Tunnels.RemoveAll(x => x.Id == rt.Profile.Id));
    }

    private void ToggleTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelRuntime rt) return;
        if (rt.IsActive) rt.Stop(); else rt.Start();
    }

    private void TunnelList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ToggleTunnel_Click(sender, e);

    #endregion

    #region Windows & menu

    private void Vault_Click(object sender, RoutedEventArgs e) => VaultWindow.Open();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsWindow.Open())
        {
            ApplySettingsToOpenTerminals();
            ShowToast("Settings saved.");
        }
    }

    private void ApplySettingsToOpenTerminals()
    {
        var s = AppServices.Settings;
        foreach (var t in AllTabs.SelectMany(t => t.Leaves).Select(l => l.Host).OfType<TerminalSessionHost>())
        {
            t.Terminal.CopyOnSelect = s.CopyOnSelect;
            t.Terminal.RightClickPastes = s.RightClickPastes;
            t.Terminal.WordDelimiters = s.WordDelimiters;
            t.Terminal.BlinkCursor = s.CursorBlink;
            if (t.Session.Terminal.KeywordHighlighting == null) t.Terminal.KeywordHighlighting = s.KeywordHighlighting;
            t.Emulator.AllowClipboardWrite = s.AllowOsc52Clipboard;
            if (t.Session.Terminal.FontFamily == null || t.Session.Terminal.FontSize == null)
                t.Terminal.SetFont(t.Session.Terminal.FontFamily ?? s.TerminalFontFamily, t.Session.Terminal.FontSize ?? s.TerminalFontSize);
            if (t.Session.Terminal.ThemeName == null)
                t.Terminal.SetTheme((SessionForge.Terminal.TerminalTheme.BuiltIn.FirstOrDefault(x => x.Name == s.TerminalTheme) ?? SessionForge.Terminal.TerminalTheme.ForgeDark()).Clone());
        }
        AppServices.Vault.AutoLockAfter = TimeSpan.FromMinutes(Math.Max(0, s.VaultAutoLockMinutes));
    }

    private void KnownHosts_Click(object sender, RoutedEventArgs e) => KnownHostsWindow.Open();

    private void KeyGen_Click(object sender, RoutedEventArgs e) => KeyManagerWindow.Open();

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        MenuItem Item(ItemsControl parent, string header, Action a, string? gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            parent.Items.Add(mi);
            return mi;
        }
        var session = new MenuItem { Header = "Session" };
        Item(session, "New session…", () => NewSessionIn(null), "Ctrl+Shift+N");
        Item(session, "New folder…", () => NewFolderIn(null));
        Item(session, "Local terminal", () => _ = OpenSession(LocalShellSession()), "Ctrl+Shift+T");
        Item(session, "Reconnect active", () => _ = _current?.ActiveHost.ReconnectAsync(), "Ctrl+Shift+R", _current != null);
        Item(session, "Close active pane", () => { if (_current != null) ClosePane(_current.ActiveHost, true); }, "Ctrl+Shift+W", _current != null);
        session.Items.Add(new Separator());
        Item(session, "Import from OpenSSH config…", () => ImportOpenSsh_Click(this, new RoutedEventArgs()));
        Item(session, "Import PuTTY sessions", () => ImportPutty_Click(this, new RoutedEventArgs()));
        Item(session, "Import SessionForge export…", () => ImportJson_Click(this, new RoutedEventArgs()));
        Item(session, "Export all sessions…", () => ExportSessions(null));
        m.Items.Add(session);

        var view = new MenuItem { Header = "View" };
        Item(view, "Toggle sidebar", () => SetSidebarVisible(SidebarColumn.Width.Value == 0), "Ctrl+Shift+B");
        Item(view, "Full screen", ToggleFullscreen, "F11");
        Item(view, "Split right", () => SplitCurrentDuplicate(OpenMode.SplitRight), "Ctrl+Shift+D");
        Item(view, "Split down", () => SplitCurrentDuplicate(OpenMode.SplitDown), "Ctrl+Shift+E");
        Item(view, "Multi-execution", () => SetMultiExec(!_multiExec), "Ctrl+Shift+M").IsChecked = _multiExec;
        view.Items.Add(new Separator());
        Item(view, ThemeManager.IsDark ? "Light theme" : "Dark theme", () =>
        {
            var t = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
            AppServices.SettingsStore.Update(s => s.Theme = t);
            ThemeManager.Apply(t);
        });
        m.Items.Add(view);

        var tools = new MenuItem { Header = "Tools" };
        Item(tools, "Credential vault…", VaultWindow.Open);
        Item(tools, "SSH keys…", KeyManagerWindow.Open);
        Item(tools, "Known hosts…", KnownHostsWindow.Open);
        Item(tools, "Macros", () => ShowPanel(RailMacros));
        Item(tools, "Tunnels", () => ShowPanel(RailTunnels));
        tools.Items.Add(new Separator());
        Item(tools, "Lock vault now", () => AppServices.Vault.Lock(), enabled: AppServices.Vault.IsUnlocked);
        Item(tools, "Open log folder", () => OpenFolder(AppServices.Settings.LogDirectory ?? AppServices.Paths.LogDirectory));
        Item(tools, "Open data folder", () => OpenFolder(AppServices.Paths.Root));
        m.Items.Add(tools);

        m.Items.Add(new Separator());
        Item(m, "Settings…", () => Settings_Click(this, new RoutedEventArgs()));
        Item(m, "About SessionForge", AboutWindow.Open);
        Item(m, "Exit", Close);
        m.PlacementTarget = MenuButton;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException) { MessageDialog.Error("Open folder", ex.Message); }
    }

    #endregion
}
