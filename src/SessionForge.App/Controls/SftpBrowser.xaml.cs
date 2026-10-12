using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Protocols.Sftp;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Controls;

public sealed class RemoteItem
{
    public RemoteItem(RemoteEntry e) => Entry = e;
    public RemoteEntry Entry { get; }
    public string Name => Entry.Name;
    public string Icon => Entry.IsParentLink ? "" : Entry.IsDirectory ? "" : Entry.IsSymlink ? "" : "";
    public Brush IconBrush => (Brush)Application.Current.FindResource(Entry.IsDirectory ? "Warning" : "Fg.Secondary");
    public string SizeText => Entry.IsDirectory ? string.Empty : FormatSize(Entry.Size);
    public string ModifiedText => Entry.IsParentLink ? string.Empty : Entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string OwnerText => Entry.IsParentLink ? string.Empty : $"{Entry.UserId}:{Entry.GroupId}";

    public static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

public sealed partial class TransferVm : ObservableObject
{
    private readonly TransferItem _item;
    public TransferVm(TransferItem item) { _item = item; CancelCommand = new RelayCommand(item.Cancel); Refresh(); }
    public TransferItem Item => _item;
    public IRelayCommand CancelCommand { get; }
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private double _percent;

    public void Refresh()
    {
        Title = $"{(_item.Direction == TransferDirection.Upload ? "↑" : "↓")} {_item.Name}";
        Percent = _item.Progress * 100;
        var speed = _item.BytesPerSecond > 0 ? $" — {RemoteItem.FormatSize((long)_item.BytesPerSecond)}/s" : string.Empty;
        var files = _item.FilesTotal > 1 ? $" — {_item.FilesDone}/{_item.FilesTotal} files" : string.Empty;
        Detail = _item.Status switch
        {
            TransferStatus.Running => $"{RemoteItem.FormatSize(_item.TransferredBytes)} of {RemoteItem.FormatSize(_item.TotalBytes)}{speed}{files}",
            TransferStatus.Failed => "Failed: " + _item.Error,
            _ => _item.Status.ToString() + files,
        };
    }
}

/// <summary>Remote file browser over SFTP with transfers and edit-in-place.</summary>
public partial class SftpBrowser : UserControl, IAsyncDisposable
{
    private readonly ObservableCollection<RemoteItem> _items = [];
    private readonly ObservableCollection<TransferVm> _transfers = [];
    private readonly List<FileSystemWatcher> _editWatchers = [];
    private SftpService? _sftp;
    private TransferQueue? _queue;
    private string _path = "/";
    private string _home = "/";
    private Point _dragStart;

    public SftpBrowser()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        TransferList.ItemsSource = _transfers;
        DragEnter += OnDragEnter;
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        KeyDown += (_, e) => { if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; } };
        FollowToggle.IsChecked = true;
    }

    public bool IsConnected => _sftp?.IsConnected == true;
    public string CurrentPath => _path;
    public bool FollowTerminal => FollowToggle.IsChecked == true;
    public event Action<string>? Notice;

    public async Task ConnectAsync(SshConnectionContext ctx, string? startPath = null)
    {
        SetBusy("Opening SFTP…");
        try
        {
            var client = await Task.Run(() => ctx.OpenSftpAsync(CancellationToken.None));
            _sftp = new SftpService(client);
            _queue = new TransferQueue(ct => ctx.OpenSftpAsync(ct), AppServices.Settings.MaxConcurrentTransfers)
            {
                PreserveTimestamps = AppServices.Settings.PreserveFileTimesOnTransfer,
            };
            _queue.ItemChanged += item => Dispatcher.BeginInvoke(() => OnTransferChanged(item));
            _queue.ItemCompleted += item => Dispatcher.BeginInvoke(() => OnTransferCompleted(item));
            _home = await _sftp.GetHomeAsync();
            await NavigateAsync(startPath ?? _home);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetBusy(null);
            ShowError("SFTP", ex);
        }
    }

    private void SetBusy(string? text)
    {
        BusyOverlay.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        BusyText.Text = text ?? string.Empty;
    }

    private static void ShowError(string title, Exception ex) => MessageDialog.Error(title, ex.Message);

    public async Task NavigateAsync(string path)
    {
        if (_sftp == null) return;
        SetBusy("Loading…");
        try
        {
            var entries = await _sftp.ListAsync(path, CancellationToken.None);
            _path = path;
            PathBox.Text = path;
            _items.Clear();
            bool showHidden = HiddenToggle.IsChecked == true;
            foreach (var e in entries)
            {
                if (!showHidden && e.Name.StartsWith('.') && !e.IsParentLink) continue;
                if (e.IsParentLink && path == "/") continue;
                _items.Add(new RemoteItem(e));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PathBox.Text = _path;
            Notice?.Invoke($"Cannot open {path}: {ex.Message}");
            MessageDialog.Error("SFTP", $"Cannot open {path}: {ex.Message}");
        }
        finally { SetBusy(null); }
    }

    public Task RefreshAsync() => NavigateAsync(_path);

    /// <summary>Called when the terminal reports a new working directory (OSC 7).</summary>
    public void TerminalDirectoryChanged(string dir)
    {
        if (FollowTerminal && IsConnected && dir != _path) _ = NavigateAsync(dir);
    }

    private IEnumerable<RemoteEntry> Selected => List.SelectedItems.Cast<RemoteItem>().Select(i => i.Entry).Where(e => !e.IsParentLink);

    #region Toolbar handlers

    private void Up_Click(object sender, RoutedEventArgs e) => _ = NavigateAsync(SftpService.Parent(_path));
    private void Home_Click(object sender, RoutedEventArgs e) => _ = NavigateAsync(_home);
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = NavigateAsync(string.IsNullOrWhiteSpace(PathBox.Text) ? "/" : PathBox.Text.Trim()); e.Handled = true; }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        var name = PromptDialog.AskText("New folder", $"Create a folder in {_path}", "Folder name");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.Contains('/')) { MessageDialog.Error("New folder", "The name cannot contain '/'."); return; }
        try { await _sftp.CreateDirectoryAsync(SftpService.Combine(_path, name), CancellationToken.None); await RefreshAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError("New folder", ex); }
    }

    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        var name = PromptDialog.AskText("New file", $"Create an empty file in {_path}", "File name");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await _sftp.CreateEmptyFileAsync(SftpService.Combine(_path, name), CancellationToken.None); await RefreshAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError("New file", ex); }
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = $"Upload to {_path}" };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) EnqueueUpload(f, false);
    }

    private void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = $"Upload folder to {_path}", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var d in dlg.FolderNames) EnqueueUpload(d, true);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected.ToList();
        if (sel.Count == 0) return;
        var dlg = new OpenFolderDialog { Title = "Download to folder" };
        if (dlg.ShowDialog() != true) return;
        foreach (var entry in sel) EnqueueDownload(entry, Path.Combine(dlg.FolderName, SafeLocalName(entry.Name)));
    }

    private static string SafeLocalName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name is "." or ".." ? "_" : name;
    }

    public void EnqueueUpload(string localPath, bool isDirectory)
    {
        if (_queue == null) return;
        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        _queue.Enqueue(new TransferItem
        {
            Direction = TransferDirection.Upload, LocalPath = localPath, RemotePath = SftpService.Combine(_path, name), IsDirectory = isDirectory,
            Conflict = ConflictPolicy.Overwrite,
        });
    }

    public void EnqueueDownload(RemoteEntry entry, string localPath)
    {
        _queue?.Enqueue(new TransferItem
        {
            Direction = TransferDirection.Download, LocalPath = localPath, RemotePath = entry.FullPath, IsDirectory = entry.IsDirectory && !entry.IsSymlink,
            Conflict = ConflictPolicy.Overwrite,
        });
    }

    #endregion

    #region List handlers

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not RemoteItem item) return;
        if (item.Entry.IsParentLink) _ = NavigateAsync(SftpService.Parent(_path));
        else if (item.Entry.IsDirectory) _ = NavigateAsync(item.Entry.FullPath);
        else _ = OpenAsync(item.Entry, watchForChanges: false);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: List_DoubleClick(sender, null!); e.Handled = true; break;
            case Key.Back: _ = NavigateAsync(SftpService.Parent(_path)); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.F2: Rename_Click(sender, e); e.Handled = true; break;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { IsDirectory: false } entry) _ = OpenAsync(entry, false);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { IsDirectory: false } entry) _ = OpenAsync(entry, true);
    }

    /// <summary>Downloads to a private temp folder and opens with the default app; optionally re-uploads on save.</summary>
    private async Task OpenAsync(RemoteEntry entry, bool watchForChanges)
    {
        if (_sftp == null) return;
        try
        {
            SetBusy($"Downloading {entry.Name}…");
            var data = await _sftp.ReadAllBytesAsync(entry.FullPath, 200L * 1024 * 1024, CancellationToken.None);
            var dir = Path.Combine(Path.GetTempPath(), "SessionForge", "edit", Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, SafeLocalName(entry.Name));
            await File.WriteAllBytesAsync(local, data);
            SetBusy(null);
            if (watchForChanges) WatchAndUpload(local, entry.FullPath);
            var editor = Environment.GetEnvironmentVariable("SESSIONFORGE_EDITOR");
            var psi = watchForChanges && !string.IsNullOrEmpty(editor)
                ? new System.Diagnostics.ProcessStartInfo(editor, $"\"{local}\"") { UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo(local) { UseShellExecute = true, Verb = watchForChanges ? "edit" : string.Empty };
            try { System.Diagnostics.Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception)
            {
                // No "edit" verb registered for this type: fall back to Notepad.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = false });
            }
            if (watchForChanges) Notice?.Invoke($"Editing {entry.FullPath} — changes are uploaded automatically when you save.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetBusy(null);
            ShowError("Open file", ex);
        }
    }

    private void WatchAndUpload(string localPath, string remotePath)
    {
        var w = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        CancellationTokenSource? pending = null;
        void Changed()
        {
            pending?.Cancel();
            var cts = pending = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600, cts.Token); // debounce editors that write in several steps
                    byte[] data = [];
                    for (int i = 0; i < 10; i++)
                    {
                        try { data = await File.ReadAllBytesAsync(localPath, cts.Token); break; }
                        catch (IOException) { await Task.Delay(200, cts.Token); }
                    }
                    if (_sftp == null) return;
                    await _sftp.WriteAllBytesAsync(remotePath, data, cts.Token);
                    await Dispatcher.InvokeAsync(() => Notice?.Invoke($"Uploaded {remotePath} ({RemoteItem.FormatSize(data.Length)})"));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() => MessageDialog.Error("Upload failed", $"{remotePath} could not be uploaded: {ex.Message}"));
                }
            });
        }
        w.Changed += (_, _) => Changed();
        w.Renamed += (_, e) => { if (string.Equals(e.FullPath, localPath, StringComparison.OrdinalIgnoreCase)) Changed(); };
        _editWatchers.Add(w);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null || Selected.FirstOrDefault() is not { } entry) return;
        var name = PromptDialog.AskText("Rename", $"Rename {entry.Name}", "New name", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        try { await _sftp.RenameAsync(entry.FullPath, SftpService.Combine(SftpService.Parent(entry.FullPath), name), CancellationToken.None); await RefreshAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError("Rename", ex); }
    }

    private async void Chmod_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null || Selected.FirstOrDefault() is not { } entry) return;
        var mode = ChmodDialog.Ask(entry.Name, entry.Mode);
        if (mode == null) return;
        try
        {
            foreach (var s in Selected.ToList()) await _sftp.ChmodAsync(s.FullPath, mode.Value);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError("Permissions", ex); }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = string.Join(Environment.NewLine, Selected.Select(s => s.FullPath));
        if (paths.Length > 0) Clipboard.SetText(paths);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        var sel = Selected.ToList();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"'{sel[0].Name}'{(sel[0].IsDirectory ? " and everything inside it" : string.Empty)}" : $"{sel.Count} items";
        if (!MessageDialog.Confirm("Delete", $"Permanently delete {what} on the server?", "Delete", "Cancel", MessageKind.Warning)) return;
        SetBusy("Deleting…");
        try { foreach (var s in sel) await _sftp.DeleteAsync(s, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError("Delete", ex); }
        finally { SetBusy(null); await RefreshAsync(); }
    }

    #endregion

    #region Drag and drop

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && IsConnected)
        {
            e.Effects = DragDropEffects.Copy;
            DropHint.Visibility = Visibility.Visible;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) EnqueueUpload(p, true);
            else if (File.Exists(p)) EnqueueUpload(p, false);
        }
        e.Handled = true;
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Dragging remote items out: provide a text list of paths (useful for dropping into terminals/editors).
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var sel = Selected.ToList();
        if (sel.Count == 0) return;
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, string.Join(" ", sel.Select(s => s.FullPath)));
        data.SetData("SessionForge.RemoteEntries", sel.ToArray());
        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy);
    }

    #endregion

    #region Transfers

    private void OnTransferChanged(TransferItem item)
    {
        var vm = _transfers.FirstOrDefault(t => t.Item == item);
        if (vm == null) { vm = new TransferVm(item); _transfers.Insert(0, vm); }
        vm.Refresh();
        TransfersPanel.Visibility = Visibility.Visible;
    }

    private void OnTransferCompleted(TransferItem item)
    {
        OnTransferChanged(item);
        if (item.Status == TransferStatus.Completed)
        {
            Notice?.Invoke($"{(item.Direction == TransferDirection.Upload ? "Uploaded" : "Downloaded")} {item.Name}");
            if (item.Direction == TransferDirection.Upload) _ = RefreshAsync();
        }
        else if (item.Status == TransferStatus.Failed) Notice?.Invoke($"Transfer of {item.Name} failed: {item.Error}");
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e)
    {
        _queue?.ClearFinished();
        foreach (var t in _transfers.Where(t => t.Item.Status is not (TransferStatus.Running or TransferStatus.Queued)).ToList()) _transfers.Remove(t);
        if (_transfers.Count == 0) TransfersPanel.Visibility = Visibility.Collapsed;
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _editWatchers) w.Dispose();
        _editWatchers.Clear();
        if (_queue != null) await _queue.DisposeAsync();
        _queue = null;
        _sftp?.Dispose();
        _sftp = null;
        _items.Clear();
    }
}
