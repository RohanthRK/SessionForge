using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SessionForge.App.Controls;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.App.Workspace;
using SessionForge.Core.Import;
using SessionForge.Core.Models;

namespace SessionForge.App.Views;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<WorkspaceTab> _tabs = [];
    private readonly Dictionary<TerminalSessionHost, SftpBrowser> _sftpBrowsers = [];
    private readonly DispatcherTimer _toastTimer;
    private WorkspaceTab? _current;
    private bool _multiExec;
    private bool _fullscreen;
    private WindowState _preFullscreenState;

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer!.Stop(); }, Dispatcher);

        RestorePlacement();
        StateChanged += (_, _) => UpdateMaximizeState();
        SourceInitialized += (_, _) => UpdateMaximizeState();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;

        AppServices.Sessions.Changed += () => Dispatcher.BeginInvoke(() => { RefreshTree(); RefreshRecent(); });
        AppServices.Macros.Changed += () => Dispatcher.BeginInvoke(RefreshMacros);
        AppServices.Tunnels.Changed += () => Dispatcher.BeginInvoke(RefreshTunnels);
        AppServices.Vault.LockStateChanged += () => Dispatcher.BeginInvoke(UpdateVaultStatus);
        AppServices.Interaction.Notice += m => Dispatcher.BeginInvoke(() => ShowToast(m));
        ThemeManager.ThemeChanged += () => { RefreshTree(); };

        if (AppServices.Settings.ShowSidebar) RailSessions.IsChecked = true; // raises Rail_Checked
        else ShowSidebarColumn(false);
        ApplySidebarVisibility();
        RefreshTree();
        RefreshRecent();
        RefreshMacros();
        RefreshTunnels();
        UpdateVaultStatus();
        UpdateHome();
        Loaded += (_, _) => OnLoaded();
    }

    private void OnLoaded()
    {
        StartAutoTunnels();
        var s = AppServices.Settings;
        if (s.RestoreSessionsOnStartup)
            foreach (var id in s.LastOpenSessions.Distinct().Take(20))
                if (AppServices.Sessions.Get(id) is { } session) _ = OpenSession(session);
        // Command line: SessionForge.exe "Session name" | ssh://user@host
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        foreach (var a in args)
        {
            var byName = AppServices.Sessions.Sessions.FirstOrDefault(x => string.Equals(x.Name, a, StringComparison.OrdinalIgnoreCase));
            if (byName != null) _ = OpenSession(byName);
            else if (QuickConnect.TryParse(a, out var quick)) _ = OpenSession(quick!);
        }
    }

    #region Window chrome & placement

    private void RestorePlacement()
    {
        var s = AppServices.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
            s.WindowLeft > SystemParameters.VirtualScreenLeft - 50 && s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            s.WindowTop > SystemParameters.VirtualScreenTop - 50 && s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        _sidebarWidth = Math.Clamp(s.SidebarWidth, 160, 600);
        SidebarColumn.Width = new GridLength(_sidebarWidth);
    }

    private void SavePlacement()
    {
        var bounds = RestoreBounds;
        AppServices.SettingsStore.Update(s =>
        {
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (!bounds.IsEmpty)
            {
                s.WindowLeft = bounds.Left; s.WindowTop = bounds.Top; s.WindowWidth = bounds.Width; s.WindowHeight = bounds.Height;
            }
            s.LastOpenSessions = AllTabs.SelectMany(t => t.Leaves).Select(l => l.Host.Session.Id).Where(id => AppServices.Sessions.Get(id) != null).ToList();
        });
    }

    private void UpdateMaximizeState()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";
        // With a custom chrome, a maximized window overhangs the screen by the resize border.
        RootGrid.Margin = max && !_fullscreen ? new Thickness(7) : new Thickness(0);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _preFullscreenState = WindowState;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);
            StatusBar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ShowToast("Full screen — press F11 to exit.");
        }
        else
        {
            RootGrid.RowDefinitions[0].Height = new GridLength(40);
            StatusBar.Visibility = Visibility.Visible;
            WindowState = _preFullscreenState;
        }
        UpdateMaximizeState();
    }

    private bool _closingConfirmed;

    /// <summary>First close request: confirm, then shut sessions down asynchronously and close again.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        e.Cancel = true;
        int connected = AllTabs.SelectMany(t => t.Leaves).Count(l => l.Host.State == HostState.Connected);
        if (connected > 0 && AppServices.Settings.ConfirmCloseConnectedTab &&
            !MessageDialog.Confirm("Exit SessionForge", $"{connected} session{(connected == 1 ? " is" : "s are")} still connected. Disconnect and exit?", "Exit", "Cancel", MessageKind.Warning))
            return;
        _closingConfirmed = true;
        SavePlacement();
        await ShutdownHostsAsync();
        Close();
    }

    private async Task ShutdownHostsAsync()
    {
        var hosts = AllTabs.SelectMany(t => t.Leaves).Select(l => l.Host).ToList();
        foreach (var w in _detached.ToList()) w.CloseWithoutReattach();
        _detached.Clear();
        foreach (var b in _sftpBrowsers.Values) await b.DisposeAsync();
        _sftpBrowsers.Clear();
        StopAllTunnels();
        var tasks = hosts.Select(h => h.DisposeAsync().AsTask());
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(3000));
    }

    #endregion

    #region Opening sessions

    public enum OpenMode { NewTab, SplitRight, SplitDown }

    private static SessionHost CreateHost(Session s) => s.Kind switch
    {
        SessionKind.Rdp => new RdpSessionHost(s),
        SessionKind.Vnc => new VncSessionHost(s),
        SessionKind.Sftp => new SftpSessionHost(s),
        _ => new TerminalSessionHost(s),
    };

    public async Task OpenSession(Session session, OpenMode mode = OpenMode.NewTab)
    {
        SessionHost host;
        try { host = CreateHost(session); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageDialog.Error("Open session", ex.Message);
            return;
        }
        WireHost(host);
        if (mode == OpenMode.NewTab || _current == null || (session.StartMode != TabStartMode.Normal && mode == OpenMode.NewTab))
        {
            var tab = new WorkspaceTab(host);
            WireTab(tab);
            int insertAt = _current != null ? _tabs.IndexOf(_current) + 1 : _tabs.Count;
            _tabs.Insert(Math.Clamp(insertAt, 0, _tabs.Count), tab);
            TabHost.Children.Add(tab.Content);
            SelectTab(tab);
        }
        else
        {
            _current.Split(host, mode == OpenMode.SplitRight ? Orientation.Vertical : Orientation.Horizontal);
            ApplyMultiExec();
            UpdateStatus();
        }
        UpdateOpenCounts();
        if (session.StartMode == TabStartMode.Fullscreen && !_fullscreen) ToggleFullscreen();
        await host.ConnectAsync();
        UpdateStatus();
        UpdateOpenCounts();
    }

    private void WireHost(SessionHost host)
    {
        host.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            UpdateOpenCounts();
            if (host is TerminalSessionHost th && th.State == HostState.Connected && th.SupportsSftp && th.Session.Ssh.OpenSftpBrowser && AppServices.Settings.AutoOpenSftp)
                EnsureSftpBrowser(th, activate: false);
        });
        host.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        host.CloseRequested += () => Dispatcher.BeginInvoke(() => ClosePane(host, confirm: false));
        switch (host)
        {
            case TerminalSessionHost t:
                t.Notice += m => Dispatcher.BeginInvoke(() => ShowStatusNotice(m));
                t.UserInput += OnTerminalInput;
                t.EditRequested += s => Dispatcher.BeginInvoke(() => EditSession(s));
                t.OpenSftpRequested += th => Dispatcher.BeginInvoke(() => EnsureSftpBrowser(th, activate: true));
                t.DuplicateRequested += th => Dispatcher.BeginInvoke(() => _ = OpenSession(th.Session, OpenMode.NewTab));
                t.WorkingDirectoryChanged += dir => { if (_sftpBrowsers.TryGetValue(t, out var b)) b.TerminalDirectoryChanged(dir); };
                t.Terminal.ShortcutHandler = TerminalShortcut;
                break;
            case SftpSessionHost s:
                s.Notice += m => Dispatcher.BeginInvoke(() => ShowStatusNotice(m));
                break;
        }
    }

    private void WireTab(WorkspaceTab tab)
    {
        tab.ActivePaneChanged += t => { if (t == _current) { UpdateStatus(); UpdateSftpPanel(); } };
        tab.PaneClosing += (t, leaf) => ClosePane(leaf.Host, confirm: true);
        tab.Emptied += t =>
        {
            if (_detached.FirstOrDefault(d => d.Tab == t) is { } w) { _detached.Remove(w); w.CloseWithoutReattach(); }
            else RemoveTab(t);
        };
    }

    private readonly List<DetachedWindow> _detached = [];

    /// <summary>All tabs, including those shown in detached windows.</summary>
    private IEnumerable<WorkspaceTab> AllTabs => _tabs.Concat(_detached.Select(d => d.Tab));

    private void DetachTab(WorkspaceTab tab)
    {
        if (tab.Leaves.Any(l => l.Host is RdpSessionHost))
        {
            ShowToast("Remote Desktop tabs can't be moved to another window (the RDP control can't be re-parented safely).");
            return;
        }
        RemoveTab(tab);
        tab.IsSelected = false;
        var w = new DetachedWindow(tab, ReattachTab);
        _detached.Add(w);
        w.Show();
    }

    private void ReattachTab(WorkspaceTab tab)
    {
        _detached.RemoveAll(d => d.Tab == tab);
        _tabs.Add(tab);
        TabHost.Children.Add(tab.Content);
        SelectTab(tab);
        Activate();
    }

    private void EditSession(Session s)
    {
        if (AppServices.Sessions.Get(s.Id) == null)
        {
            MessageDialog.Info("Edit session", "This is a quick-connect session that hasn't been saved.");
            return;
        }
        var (saved, connect) = SessionEditorWindow.Run(s, isNew: false);
        if (saved != null && connect) _ = OpenSession(saved);
    }

    #endregion

    #region Tabs & panes

    private void SelectTab(WorkspaceTab? tab)
    {
        _current = tab;
        foreach (var t in _tabs)
        {
            t.IsSelected = t == tab;
            t.Content.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateHome();
        ApplyMultiExec();
        UpdateStatus();
        UpdateSftpPanel();
        if (tab != null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => tab.ActiveHost.FocusView());
            // Make sure the selected tab header is visible.
            if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement fe) fe.BringIntoView();
        }
    }

    private void UpdateHome()
    {
        HomeView.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes a single pane (or the whole tab if it's the last pane).</summary>
    public async void ClosePane(SessionHost host, bool confirm)
    {
        var tab = AllTabs.FirstOrDefault(t => t.Leaves.Any(l => l.Host == host));
        if (tab == null) return;
        if (confirm && host.State == HostState.Connected && AppServices.Settings.ConfirmCloseConnectedTab &&
            !MessageDialog.Confirm("Close session", $"'{host.Title}' is connected. Disconnect and close it?", "Close", "Cancel", MessageKind.Question))
            return;
        var leaf = tab.Leaves.First(l => l.Host == host);
        tab.Remove(leaf);
        await DisposeHostAsync(host);
        UpdateOpenCounts();
    }

    private async Task DisposeHostAsync(SessionHost host)
    {
        if (host is TerminalSessionHost th && _sftpBrowsers.Remove(th, out var browser))
        {
            if (SftpPanelHost.Children.Contains(browser)) SftpPanelHost.Children.Remove(browser);
            await browser.DisposeAsync();
        }
        try { await host.DisposeAsync(); }
        catch (Exception ex) { Log.Warn($"Dispose failed: {ex.Message}"); }
    }

    private void RemoveTab(WorkspaceTab tab)
    {
        int idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabHost.Children.Remove(tab.Content);
        if (_current == tab) SelectTab(_tabs.Count == 0 ? null : _tabs[Math.Clamp(idx, 0, _tabs.Count - 1)]);
        UpdateHome();
    }

    public async Task CloseTab(WorkspaceTab tab, bool confirm = true)
    {
        var hosts = tab.Leaves.Select(l => l.Host).ToList();
        int connected = hosts.Count(h => h.State == HostState.Connected);
        if (confirm && connected > 0 && AppServices.Settings.ConfirmCloseConnectedTab &&
            !MessageDialog.Confirm("Close tab", connected == 1 ? $"'{tab.Header}' is connected. Disconnect and close the tab?" : $"This tab has {connected} connected sessions. Disconnect and close?", "Close", "Cancel"))
            return;
        RemoveTab(tab);
        foreach (var h in hosts) await DisposeHostAsync(h);
        UpdateOpenCounts();
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkspaceTab tab)
        {
            _dragTab = tab;
            _dragStart = e.GetPosition(this);
            SelectTab(tab);
            if (e.ClickCount == 2) RenameTab(tab);
        }
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceTab tab) return;
        if (e.ChangedButton == MouseButton.Middle) { _ = CloseTab(tab); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.Right) { ShowTabMenu(tab, (FrameworkElement)sender); e.Handled = true; }
    }

    private WorkspaceTab? _dragTab;
    private Point _dragStart;

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragTab == null) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("SessionForge.Tab", _dragTab), DragDropEffects.Move);
        _dragTab = null;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("SessionForge.Tab") is not WorkspaceTab moving) return;
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceTab target || target == moving) return;
        int from = _tabs.IndexOf(moving), to = _tabs.IndexOf(target);
        if (from >= 0 && to >= 0) _tabs.Move(from, to);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkspaceTab tab) _ = CloseTab(tab);
        e.Handled = true;
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void TabList_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var t in _tabs)
        {
            var tab = t;
            var mi = new MenuItem { Header = t.Header, IsChecked = t == _current };
            mi.Click += (_, _) => SelectTab(tab);
            menu.Items.Add(mi);
        }
        if (_tabs.Count == 0) menu.Items.Add(new MenuItem { Header = "(no open tabs)", IsEnabled = false });
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void ShowTabMenu(WorkspaceTab tab, FrameworkElement anchor)
    {
        var m = new ContextMenu();
        MenuItem Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); m.Items.Add(mi); return mi; }
        Item("Reconnect", () => _ = tab.ActiveHost.ReconnectAsync());
        Item("Duplicate", () => _ = OpenSession(tab.ActiveHost.Session));
        Item("Rename tab…", () => RenameTab(tab));
        Item("Edit session…", () => EditSession(tab.ActiveHost.Session), AppServices.Sessions.Get(tab.ActiveHost.Session.Id) != null);
        m.Items.Add(new Separator());
        Item("Split right: duplicate", () => { SelectTab(tab); _ = OpenSession(tab.ActiveHost.Session, OpenMode.SplitRight); });
        Item("Split down: duplicate", () => { SelectTab(tab); _ = OpenSession(tab.ActiveHost.Session, OpenMode.SplitDown); });
        Item("Rotate split", () => tab.RotateActiveSplit(), tab.PaneCount > 1);
        Item("Move to new window", () => DetachTab(tab));
        m.Items.Add(new Separator());
        Item("Close", () => _ = CloseTab(tab));
        Item("Close other tabs", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) _ = CloseTab(t); }, _tabs.Count > 1);
        Item("Close tabs to the right", () => { foreach (var t in _tabs.Skip(_tabs.IndexOf(tab) + 1).ToList()) _ = CloseTab(t); }, _tabs.IndexOf(tab) < _tabs.Count - 1);
        m.PlacementTarget = anchor;
        m.IsOpen = true;
    }

    private void RenameTab(WorkspaceTab tab)
    {
        var name = PromptDialog.AskText("Rename tab", "Leave empty to use the session title.", "Tab title", tab.CustomTitle ?? tab.Header);
        if (name == null) return;
        tab.CustomTitle = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private void CycleTab(int dir)
    {
        if (_tabs.Count < 2 || _current == null) return;
        int i = _tabs.IndexOf(_current);
        SelectTab(_tabs[(i + dir + _tabs.Count) % _tabs.Count]);
    }

    private void SplitVertical_Click(object sender, RoutedEventArgs e) => SplitCurrent(OpenMode.SplitRight);
    private void SplitHorizontal_Click(object sender, RoutedEventArgs e) => SplitCurrent(OpenMode.SplitDown);

    /// <summary>Splits the active pane: duplicates the active session (or opens a local shell when nothing is open).</summary>
    private void SplitCurrent(OpenMode mode)
    {
        if (_current == null) { _ = OpenSession(LocalShellSession()); return; }
        var menu = new ContextMenu();
        var dup = new MenuItem { Header = $"Duplicate '{_current.ActiveHost.Session.Name}'" };
        dup.Click += (_, _) => _ = OpenSession(_current.ActiveHost.Session, mode);
        menu.Items.Add(dup);
        var local = new MenuItem { Header = "Local terminal" };
        local.Click += (_, _) => _ = OpenSession(LocalShellSession(), mode);
        menu.Items.Add(local);
        var pick = new MenuItem { Header = "Saved session…" };
        pick.Click += (_, _) => { var s = SessionPickerDialog.Pick("Open in split pane", _ => true); if (s != null) _ = OpenSession(s, mode); };
        menu.Items.Add(pick);
        menu.IsOpen = true;
    }

    #endregion

    #region Multi-execution

    private void MultiExec_Click(object sender, RoutedEventArgs e) => SetMultiExec(MultiExecToggle.IsChecked == true);

    private void SetMultiExec(bool on)
    {
        _multiExec = on;
        MultiExecToggle.IsChecked = on;
        ApplyMultiExec();
        if (on)
        {
            int n = _current?.Leaves.Count(l => l.Host.IsTerminal) ?? 0;
            ShowToast(n > 1 ? $"Multi-execution ON: typing goes to all {n} terminals in this tab." : "Multi-execution ON. Split this tab into several terminals to type into all of them at once.");
        }
    }

    private void ApplyMultiExec()
    {
        foreach (var tab in _tabs)
        {
            bool active = _multiExec && tab == _current;
            tab.MultiExecHighlight = active;
            foreach (var l in tab.Leaves)
                if (l.Host is TerminalSessionHost t) t.MultiExecActive = active;
            tab.RefreshFrames();
        }
    }

    private void OnTerminalInput(TerminalSessionHost source, string text)
    {
        if (_recording != null && _current?.ActiveHost == source) RecordInput(text);
        if (!_multiExec || _current == null || !source.MultiExecActive) return;
        foreach (var l in _current.Leaves)
            if (l.Host is TerminalSessionHost t && t.State == HostState.Connected) t.WriteToHost(text);
    }

    #endregion

    #region Keyboard

    private bool TerminalShortcut(KeyEventArgs e) => false; // window-level PreviewKeyDown handles app shortcuts first

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        bool cs = m == (ModifierKeys.Control | ModifierKeys.Shift);
        bool c = m == ModifierKeys.Control;
        bool a = m == ModifierKeys.Alt;
        void Done() => e.Handled = true;

        if (key == Key.F11 && m == ModifierKeys.None) { ToggleFullscreen(); Done(); return; }
        if (c && key == Key.Tab) { CycleTab(1); Done(); return; }
        if (cs && key == Key.Tab) { CycleTab(-1); Done(); return; }
        if (cs)
        {
            switch (key)
            {
                case Key.N: NewSession_Click(this, e); Done(); return;
                case Key.T: _ = OpenSession(LocalShellSession()); Done(); return;
                case Key.W: if (_current != null) ClosePane(_current.ActiveHost, confirm: true); Done(); return;
                case Key.D: SplitCurrentDuplicate(OpenMode.SplitRight); Done(); return;
                case Key.E: SplitCurrentDuplicate(OpenMode.SplitDown); Done(); return;
                case Key.M: SetMultiExec(!_multiExec); Done(); return;
                case Key.K: QuickConnectBox.Focus(); QuickConnectBox.SelectAll(); Done(); return;
                case Key.F:
                    if (_current?.ActiveHost is TerminalSessionHost th) { th.TerminalView.ShowFind(); Done(); }
                    return;
                case Key.B: SetSidebarVisible(SidebarColumn.Width.Value == 0); Done(); return;
                case Key.R: if (_current != null) _ = _current.ActiveHost.ReconnectAsync(); Done(); return;
            }
            if (key is >= Key.D1 and <= Key.D9)
            {
                int idx = key - Key.D1;
                if (idx < _tabs.Count) SelectTab(_tabs[idx]);
                Done();
                return;
            }
        }
        if (a && _current != null && key is Key.Left or Key.Up) { if (_current.PaneCount > 1) { _current.CyclePane(-1); Done(); } return; }
        if (a && _current != null && key is Key.Right or Key.Down) { if (_current.PaneCount > 1) { _current.CyclePane(1); Done(); } return; }
        if (m != ModifierKeys.None && TryRunMacroHotkey(key, m)) Done();
    }

    private void SplitCurrentDuplicate(OpenMode mode)
    {
        if (_current == null) _ = OpenSession(LocalShellSession());
        else _ = OpenSession(_current.ActiveHost.Session, mode);
    }

    #endregion

    #region Status & notifications

    private void UpdateStatus()
    {
        var host = _current?.ActiveHost;
        StatusText.Text = host == null ? $"{AppServices.Sessions.Sessions.Count} saved sessions" : $"{host.State}   •   {host.StatusText}";
        ForwardStatus.Text = host is TerminalSessionHost { Forwards: { } f } && f.States.Count > 0
            ? $"⇄ {f.States.Count(s => s.Status == SessionForge.Protocols.Ssh.ForwardStatus.Running)}/{f.States.Count} forwards"
            : string.Empty;
        Title = host == null ? "SessionForge" : $"{host.Title} — SessionForge";
    }

    private void UpdateVaultStatus()
    {
        var v = AppServices.Vault;
        VaultStatus.Text = !v.Exists ? "Vault: not set up" : v.IsUnlocked ? "🔓 Vault unlocked" : "🔒 Vault locked";
        VaultIcon.Text = v.IsUnlocked ? "" : "";
    }

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ShowStatusNotice(string message)
    {
        // Short notices go to the status line; long or important ones become a toast.
        if (message.StartsWith("Logging", StringComparison.Ordinal) || message.StartsWith("Macro", StringComparison.Ordinal) ||
            message.StartsWith("Uploaded", StringComparison.Ordinal) || message.StartsWith("Downloaded", StringComparison.Ordinal) ||
            message.StartsWith("Editing", StringComparison.Ordinal) || message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Warning", StringComparison.Ordinal) || message.StartsWith("Could not", StringComparison.Ordinal) || message.StartsWith("Note", StringComparison.Ordinal))
            ShowToast(message);
        StatusText.Text = message;
    }

    #endregion
}
