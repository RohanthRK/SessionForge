using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.App.Workspace;
using SessionForge.Core.Import;
using SessionForge.Core.Models;

namespace SessionForge.App.Views;

public partial class MainWindow
{
    private Point _treeDragStart;
    private TreeNode? _treeDragNode;

    #region Tree

    private void RefreshTree()
    {
        var counts = OpenCounts();
        var expandedSelection = (SessionTree.SelectedItem as SessionNode)?.Session.Id;
        var nodes = SessionTreeBuilder.Build(SessionFilter.Text, counts);
        SessionTree.ItemsSource = nodes;
        if (expandedSelection is { } id)
            foreach (var n in Flatten(nodes)) if (n is SessionNode sn && sn.Session.Id == id) { sn.IsSelected = true; break; }
    }

    private static IEnumerable<TreeNode> Flatten(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private Dictionary<Guid, int> OpenCounts() =>
        AllTabs.SelectMany(t => t.Leaves).GroupBy(l => l.Host.Session.Id).ToDictionary(g => g.Key, g => g.Count());

    private void UpdateOpenCounts()
    {
        var counts = OpenCounts();
        if (SessionTree.ItemsSource is IEnumerable<TreeNode> nodes)
            foreach (var n in Flatten(nodes))
                if (n is SessionNode sn) sn.OpenCount = counts.GetValueOrDefault(sn.Session.Id);
    }

    private void RefreshRecent()
    {
        var recent = AppServices.Sessions.Recent.Take(8).ToList();
        RecentList.ItemsSource = recent;
        NoRecent.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Session s) _ = OpenSession(s);
    }

    private void SessionFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshTree();

    private void SessionFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SessionTree.ItemsSource is IEnumerable<TreeNode> nodes && Flatten(nodes).OfType<SessionNode>().FirstOrDefault() is { } first)
        {
            _ = OpenSession(first.Session);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { SessionFilter.Clear(); e.Handled = true; }
        else if (e.Key == Key.Down) { SessionTree.Focus(); e.Handled = true; }
    }

    private void SessionTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindItem(d)?.DataContext is SessionNode sn)
        {
            _ = OpenSession(sn.Session);
            e.Handled = true;
        }
    }

    private void SessionTree_KeyDown(object sender, KeyEventArgs e)
    {
        var node = SessionTree.SelectedItem as TreeNode;
        switch (e.Key)
        {
            case Key.Enter when node is SessionNode sn: _ = OpenSession(sn.Session); e.Handled = true; break;
            case Key.F2 when node != null: RenameNode(node); e.Handled = true; break;
            case Key.Delete when node != null: DeleteNode(node); e.Handled = true; break;
        }
    }

    private static TreeViewItem? FindItem(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private void SessionTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindItem(e.OriginalSource as DependencyObject);
        if (item != null) { item.IsSelected = true; item.Focus(); }
        var node = item?.DataContext as TreeNode;
        var menu = BuildTreeMenu(node);
        menu.PlacementTarget = SessionTree;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildTreeMenu(TreeNode? node)
    {
        var m = new ContextMenu();
        MenuItem Item(string header, string glyph, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
            return mi;
        }
        Guid? folderForNew = node switch { FolderNode f => f.Folder.Id, SessionNode s => s.Session.FolderId, _ => null };
        if (node is SessionNode sn)
        {
            var s = sn.Session;
            Item("Connect", "", () => _ = OpenSession(s));
            Item("Connect in split (right)", "", () => _ = OpenSession(s, OpenMode.SplitRight), _current != null);
            Item("Connect in split (down)", "", () => _ = OpenSession(s, OpenMode.SplitDown), _current != null);
            if (s.Kind == SessionKind.Ssh)
                Item("Open SFTP only", "", () => { var copy = Core.Storage.JsonFile.Clone(s); copy.Kind = SessionKind.Sftp; copy.Id = s.Id; _ = OpenSession(copy); });
            m.Items.Add(new Separator());
            Item("Edit…", "", () => EditSession(s));
            Item("Duplicate", "", () => AppServices.Sessions.Duplicate(s.Id));
            Item("Rename", "", () => RenameNode(sn));
            Item("Copy host", "", () => Clipboard.SetText(s.Kind == SessionKind.Serial ? s.Serial.PortName : s.Host));
            Item("Create desktop shortcut", "", () => CreateShortcut(s));
            m.Items.Add(new Separator());
            Item("Delete", "", () => DeleteNode(sn));
        }
        else if (node is FolderNode fn)
        {
            Item("Connect all", "", () => { foreach (var s in Flatten(fn.Children).OfType<SessionNode>().Take(30)) _ = OpenSession(s.Session); }, fn.Count > 0);
            Item("New session here…", "", () => NewSessionIn(fn.Folder.Id));
            Item("New subfolder…", "", () => NewFolderIn(fn.Folder.Id));
            Item("Rename", "", () => RenameNode(fn));
            Item("Export folder…", "", () => ExportSessions(Flatten(fn.Children).OfType<SessionNode>().Select(x => x.Session.Id).ToList()));
            m.Items.Add(new Separator());
            Item("Delete folder", "", () => DeleteNode(fn));
        }
        else
        {
            Item("New session…", "", () => NewSessionIn(null));
            Item("New folder…", "", () => NewFolderIn(null));
        }
        return m;
    }

    private void RenameNode(TreeNode node)
    {
        switch (node)
        {
            case SessionNode sn:
                var name = PromptDialog.AskText("Rename session", string.Empty, "Name", sn.Session.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                sn.Session.Name = name.Trim();
                AppServices.Sessions.AddOrUpdate(sn.Session);
                break;
            case FolderNode fn:
                var fname = PromptDialog.AskText("Rename folder", string.Empty, "Name", fn.Folder.Name);
                if (string.IsNullOrWhiteSpace(fname)) return;
                AppServices.Sessions.RenameFolder(fn.Folder.Id, fname.Trim());
                break;
        }
    }

    private void DeleteNode(TreeNode node)
    {
        switch (node)
        {
            case SessionNode sn:
                if (!MessageDialog.Confirm("Delete session", $"Delete '{sn.Session.Name}'? Saved passwords for it are removed from the vault too.", "Delete", "Cancel", MessageKind.Warning)) return;
                AppServices.Sessions.Remove(sn.Session.Id);
                if (AppServices.Vault.IsUnlocked) AppServices.Vault.RemoveSecretsWithPrefix($"session:{sn.Session.Id}:");
                break;
            case FolderNode fn:
                if (fn.Count == 0)
                {
                    AppServices.Sessions.DeleteFolder(fn.Folder.Id, deleteContents: false);
                    return;
                }
                int choice = MessageDialog.Show("Delete folder", $"'{fn.Folder.Name}' contains {fn.Count} session(s).", MessageKind.Warning,
                    ["Delete folder and sessions", "Keep sessions (move up)", "Cancel"], 1, 2);
                if (choice == 0) AppServices.Sessions.DeleteFolder(fn.Folder.Id, deleteContents: true);
                else if (choice == 1) AppServices.Sessions.DeleteFolder(fn.Folder.Id, deleteContents: false);
                break;
        }
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        Guid? folder = SessionTree.SelectedItem switch { FolderNode f => f.Folder.Id, SessionNode s => s.Session.FolderId, _ => null };
        NewSessionIn(folder);
    }

    private void NewSessionIn(Guid? folderId)
    {
        var (saved, connect) = SessionEditorWindow.Run(new Session { FolderId = folderId, Name = string.Empty }, isNew: true);
        if (saved != null && connect) _ = OpenSession(saved);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        Guid? parent = SessionTree.SelectedItem switch { FolderNode f => f.Folder.Id, SessionNode s => s.Session.FolderId, _ => null };
        NewFolderIn(parent);
    }

    private static void NewFolderIn(Guid? parentId)
    {
        var name = PromptDialog.AskText("New folder", string.Empty, "Folder name");
        if (!string.IsNullOrWhiteSpace(name)) AppServices.Sessions.AddFolder(name.Trim(), parentId);
    }

    private static void CreateShortcut(Session s)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var safe = string.Concat(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var exe = Environment.ProcessPath ?? "SessionForge.exe";
            // A small .cmd launcher passes the session name as an argument (SessionForge opens it on start).
            var path = Path.Combine(desktop, safe + " (SessionForge).cmd");
            // Strip cmd.exe metacharacters so a session name can never inject commands.
            var arg = string.Concat(s.Name.Where(c => "\"&|<>^%!".IndexOf(c) < 0 && !char.IsControl(c)));
            File.WriteAllText(path, $"@start \"\" \"{exe}\" \"{arg}\"\r\n");
            MessageDialog.Info("Shortcut created", $"Created {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageDialog.Error("Shortcut", ex.Message); }
    }

    // Drag & drop to reorganise sessions/folders
    private void SessionTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _treeDragStart = e.GetPosition(SessionTree);
        _treeDragNode = FindItem(e.OriginalSource as DependencyObject)?.DataContext as TreeNode;
    }

    private void SessionTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _treeDragNode == null || !string.IsNullOrEmpty(SessionFilter.Text)) return;
        var d = e.GetPosition(SessionTree) - _treeDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _treeDragNode;
        _treeDragNode = null;
        DragDrop.DoDragDrop(SessionTree, new DataObject("SessionForge.TreeNode", node), DragDropEffects.Move);
    }

    private void SessionTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("SessionForge.TreeNode") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SessionTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("SessionForge.TreeNode") is not TreeNode moving) return;
        var target = FindItem(e.OriginalSource as DependencyObject)?.DataContext as TreeNode;
        Guid? folder = target switch { FolderNode f => f.Folder.Id, SessionNode s => s.Session.FolderId, _ => null };
        try
        {
            switch (moving)
            {
                case SessionNode sn when sn.Session.FolderId != folder: AppServices.Sessions.MoveSession(sn.Session.Id, folder); break;
                case FolderNode fn when fn.Folder.Id != folder && fn.Folder.ParentId != folder: AppServices.Sessions.MoveFolder(fn.Folder.Id, folder); break;
            }
        }
        catch (InvalidOperationException ex) { ShowToast(ex.Message); }
        e.Handled = true;
    }

    #endregion

    #region Quick connect

    private void QuickConnect_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { QuickConnectBox.Clear(); _current?.ActiveHost.FocusView(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = QuickConnectBox.Text.Trim();
        // A saved session name takes precedence.
        var byName = AppServices.Sessions.Sessions.FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase));
        if (byName != null) { QuickConnectBox.Clear(); _ = OpenSession(byName); return; }
        if (!QuickConnect.TryParse(text, out var session))
        {
            ShowToast("Use [protocol://][user@]host[:port], e.g. admin@10.0.0.5:2222, rdp://server, serial://COM3:115200");
            return;
        }
        session!.Ssh.KeepAliveSeconds = AppServices.Settings.DefaultKeepAliveSeconds;
        QuickConnectBox.Clear();
        _ = OpenSession(session);
    }

    #endregion

    #region Import / export

    private void ImportMenu_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        void Item(string h, RoutedEventHandler a) { var mi = new MenuItem { Header = h }; mi.Click += a; m.Items.Add(mi); }
        Item("Import from OpenSSH config (~/.ssh/config)…", ImportOpenSsh_Click);
        Item("Import PuTTY sessions", ImportPutty_Click);
        Item("Import SessionForge export (.json)…", ImportJson_Click);
        m.Items.Add(new Separator());
        Item("Export all sessions…", (_, _) => ExportSessions(null));
        Item("Import OpenSSH known_hosts…", ImportKnownHosts_Click);
        m.PlacementTarget = ImportButton;
        m.IsOpen = true;
    }

    private void ImportOpenSsh_Click(object sender, RoutedEventArgs e)
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        string path = defaultPath;
        if (!File.Exists(defaultPath))
        {
            var dlg = new OpenFileDialog { Title = "OpenSSH client config", FileName = "config" };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        try
        {
            var sessions = SshConfigImporter.Parse(File.ReadAllText(path));
            ImportSessions(sessions, "OpenSSH config");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageDialog.Error("Import", ex.Message); }
    }

    private void ImportPutty_Click(object sender, RoutedEventArgs e)
    {
        var sessions = PuttyImporter.ImportFromRegistry();
        if (sessions.Count == 0) { MessageDialog.Info("Import PuTTY", "No PuTTY sessions were found for this Windows user."); return; }
        ImportSessions(sessions, "PuTTY");
    }

    private void ImportSessions(List<Session> sessions, string source)
    {
        if (sessions.Count == 0) { MessageDialog.Info("Import", $"No sessions found in {source}."); return; }
        var existing = AppServices.Sessions.Sessions.Select(s => (s.Host.ToLowerInvariant(), s.Port, s.Credential.Username)).ToHashSet();
        var fresh = sessions.Where(s => !existing.Contains((s.Host.ToLowerInvariant(), s.Port, s.Credential.Username))).ToList();
        if (fresh.Count == 0) { MessageDialog.Info("Import", $"All {sessions.Count} session(s) from {source} already exist."); return; }
        if (!MessageDialog.Confirm("Import", $"Import {fresh.Count} session(s) from {source} into a new folder? ({sessions.Count - fresh.Count} duplicates skipped)", "Import", "Cancel")) return;
        var folder = AppServices.Sessions.AddFolder($"Imported from {source}", null);
        foreach (var s in fresh)
        {
            s.FolderId = folder.Id;
            AppServices.Sessions.AddOrUpdate(s);
        }
        ShowToast($"Imported {fresh.Count} session(s).");
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SessionForge export|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = AppServices.Sessions.Import(File.ReadAllText(dlg.FileName));
            ShowToast($"Imported {n} session(s).");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { MessageDialog.Error("Import", ex.Message); }
    }

    private void ExportSessions(IReadOnlyList<Guid>? ids)
    {
        var dlg = new SaveFileDialog { Filter = "SessionForge export|*.json", FileName = $"sessionforge-sessions-{DateTime.Now:yyyyMMdd}.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, AppServices.Sessions.Export(ids));
            ShowToast("Exported. Passwords are not included in exports.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageDialog.Error("Export", ex.Message); }
    }

    private void ImportKnownHosts_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        var dlg = new OpenFileDialog { Title = "OpenSSH known_hosts", FileName = path, InitialDirectory = Path.GetDirectoryName(path) };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = AppServices.KnownHosts.ImportOpenSsh(File.ReadAllText(dlg.FileName));
            ShowToast($"Imported {n} host key(s). Hashed entries cannot be imported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageDialog.Error("Import", ex.Message); }
    }

    #endregion

    #region Local terminal

    private static Session LocalShellSession(string? command = null)
    {
        var cmd = command ?? SessionEditorWindow.LocalShells().First();
        var name = cmd.Split(' ')[0].Trim('"');
        name = Path.GetFileNameWithoutExtension(name);
        return new Session { Kind = SessionKind.LocalShell, Name = name, Shell = new LocalShellOptions { CommandLine = cmd } };
    }

    private void LocalTerminal_Click(object sender, RoutedEventArgs e) => _ = OpenSession(LocalShellSession());

    private void NewTabButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        var m = new ContextMenu();
        foreach (var shell in SessionEditorWindow.LocalShells())
        {
            var cmd = shell;
            var mi = new MenuItem { Header = cmd };
            mi.Click += (_, _) => _ = OpenSession(LocalShellSession(cmd));
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        var ns = new MenuItem { Header = "New session…" };
        ns.Click += NewSession_Click;
        m.Items.Add(ns);
        m.PlacementTarget = (UIElement)sender;
        m.IsOpen = true;
        e.Handled = true;
    }

    #endregion
}
