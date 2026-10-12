using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SessionForge.Protocols.Sftp;

public enum TransferDirection { Upload, Download }
public enum TransferStatus { Queued, Running, Completed, Failed, Cancelled, Skipped }
public enum ConflictPolicy { Overwrite, Skip, Rename, OverwriteIfNewer }

public sealed class TransferItem
{
    internal readonly CancellationTokenSource Cts = new();

    public Guid Id { get; } = Guid.NewGuid();
    public required TransferDirection Direction { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public bool IsDirectory { get; init; }
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Overwrite;

    public TransferStatus Status { get; internal set; }
    public long TotalBytes { get; internal set; }
    public long TransferredBytes { get; internal set; }
    public double BytesPerSecond { get; internal set; }
    public string? Error { get; internal set; }
    public string? CurrentFile { get; internal set; }
    public int FilesDone { get; internal set; }
    public int FilesTotal { get; internal set; }

    public string Name => Path.GetFileName(Direction == TransferDirection.Upload ? LocalPath.TrimEnd('\\', '/') : RemotePath.TrimEnd('/'));
    public double Progress => TotalBytes <= 0 ? (Status == TransferStatus.Completed ? 1 : 0) : Math.Min(1.0, (double)TransferredBytes / TotalBytes);

    public void Cancel() => Cts.Cancel();
}

/// <summary>
/// Concurrent SFTP transfer queue. Each worker uses its own SFTP connection (opened lazily through the session's
/// connection context) so transfers never block browsing. Supports recursive directories, conflict policies,
/// cancellation and preservation of modification times.
/// </summary>
public sealed class TransferQueue : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<SftpClient>> _openClient;
    private readonly List<TransferItem> _items = [];
    private readonly SemaphoreSlim _slots;
    private readonly Stack<SftpClient> _idle = new();
    private readonly List<SftpClient> _all = [];
    private readonly object _lock = new();
    private bool _disposed;

    public TransferQueue(Func<CancellationToken, Task<SftpClient>> openClient, int maxConcurrent)
    {
        _openClient = openClient;
        _slots = new SemaphoreSlim(Math.Clamp(maxConcurrent, 1, 8));
    }

    public bool PreserveTimestamps { get; set; } = true;

    public event Action<TransferItem>? ItemChanged;
    public event Action<TransferItem>? ItemCompleted;

    public IReadOnlyList<TransferItem> Items { get { lock (_lock) return _items.ToList(); } }

    public TransferItem Enqueue(TransferItem item)
    {
        lock (_lock) _items.Add(item);
        ItemChanged?.Invoke(item);
        _ = RunAsync(item);
        return item;
    }

    public void ClearFinished()
    {
        lock (_lock) _items.RemoveAll(i => i.Status is TransferStatus.Completed or TransferStatus.Cancelled or TransferStatus.Skipped or TransferStatus.Failed);
    }

    private async Task RunAsync(TransferItem item)
    {
        try
        {
            await _slots.WaitAsync(item.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Status = TransferStatus.Cancelled;
            ItemChanged?.Invoke(item);
            ItemCompleted?.Invoke(item);
            return;
        }
        SftpClient? client = null;
        try
        {
            item.Status = TransferStatus.Running;
            ItemChanged?.Invoke(item);
            client = await RentAsync(item.Cts.Token).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var progress = new ProgressTracker(item, sw, () => ItemChanged?.Invoke(item));
            if (item.Direction == TransferDirection.Upload)
            {
                if (item.IsDirectory) await UploadDirectoryAsync(client, item.LocalPath, item.RemotePath, item, progress).ConfigureAwait(false);
                else
                {
                    item.TotalBytes = new FileInfo(item.LocalPath).Length;
                    item.FilesTotal = 1;
                    await UploadFileAsync(client, item.LocalPath, item.RemotePath, item, progress).ConfigureAwait(false);
                }
            }
            else
            {
                if (item.IsDirectory) await DownloadDirectoryAsync(client, item.RemotePath, item.LocalPath, item, progress).ConfigureAwait(false);
                else
                {
                    item.TotalBytes = (await client.GetAttributesAsync(item.RemotePath, item.Cts.Token).ConfigureAwait(false)).Size;
                    item.FilesTotal = 1;
                    await DownloadFileAsync(client, item.RemotePath, item.LocalPath, item, progress).ConfigureAwait(false);
                }
            }
            if (item.Status == TransferStatus.Running) item.Status = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            item.Status = TransferStatus.Cancelled;
        }
        catch (Exception ex)
        {
            item.Status = TransferStatus.Failed;
            item.Error = ex.Message;
        }
        finally
        {
            if (client != null) Return(client);
            _slots.Release();
            ItemChanged?.Invoke(item);
            ItemCompleted?.Invoke(item);
        }
    }

    private async Task<SftpClient> RentAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            while (_idle.Count > 0)
            {
                var c = _idle.Pop();
                if (c.IsConnected) return c;
                _all.Remove(c);
                c.Dispose();
            }
        }
        var client = await _openClient(ct).ConfigureAwait(false);
        lock (_lock) _all.Add(client);
        return client;
    }

    private void Return(SftpClient c)
    {
        lock (_lock)
        {
            if (_disposed) { c.Dispose(); return; }
            _idle.Push(c);
        }
    }

    private sealed class ProgressTracker
    {
        private readonly TransferItem _item;
        private readonly Stopwatch _sw;
        private readonly Action _notify;
        private long _baseBytes;
        private long _lastNotify;

        public ProgressTracker(TransferItem item, Stopwatch sw, Action notify) { _item = item; _sw = sw; _notify = notify; }

        public void BeginFile(string name) { _item.CurrentFile = name; _baseBytes = _item.TransferredBytes; }

        public void Report(ulong fileBytes)
        {
            _item.TransferredBytes = _baseBytes + (long)fileBytes;
            var secs = _sw.Elapsed.TotalSeconds;
            if (secs > 0.2) _item.BytesPerSecond = _item.TransferredBytes / secs;
            var now = _sw.ElapsedMilliseconds;
            if (now - _lastNotify > 150) { _lastNotify = now; _notify(); }
        }
    }

    private async Task UploadFileAsync(SftpClient c, string local, string remote, TransferItem item, ProgressTracker p)
    {
        var ct = item.Cts.Token;
        remote = await ResolveConflictAsync(c, remote, File.GetLastWriteTimeUtc(local), item, ct).ConfigureAwait(false) ?? string.Empty;
        if (remote.Length == 0) { item.FilesDone++; return; }
        p.BeginFile(Path.GetFileName(local));
        await using (var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var progress = new Progress<UploadFileProgressReport>(r => p.Report(r.TotalBytesUploaded));
            await c.UploadFileAsync(fs, remote, progress, ct).ConfigureAwait(false);
            p.Report((ulong)fs.Length);
        }
        if (PreserveTimestamps)
        {
            try { c.SetLastWriteTimeUtc(remote, File.GetLastWriteTimeUtc(local)); } catch (Exception) { /* some servers refuse SETSTAT */ }
        }
        item.FilesDone++;
    }

    private async Task DownloadFileAsync(SftpClient c, string remote, string local, TransferItem item, ProgressTracker p)
    {
        var ct = item.Cts.Token;
        var attrs = await c.GetAttributesAsync(remote, ct).ConfigureAwait(false);
        if (File.Exists(local))
        {
            switch (item.Conflict)
            {
                case ConflictPolicy.Skip: item.FilesDone++; return;
                case ConflictPolicy.Rename: local = UniqueLocal(local); break;
                case ConflictPolicy.OverwriteIfNewer when File.GetLastWriteTimeUtc(local) >= attrs.LastWriteTimeUtc: item.FilesDone++; return;
            }
        }
        p.BeginFile(Path.GetFileName(remote));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(local))!);
        // Download to a temp file then move into place, so a cancelled transfer never leaves a truncated file.
        var tmp = local + ".sfpart";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var progress = new Progress<DownloadFileProgressReport>(r => p.Report(r.TotalBytesDownloaded));
                await c.DownloadFileAsync(remote, fs, progress, ct).ConfigureAwait(false);
            }
            File.Move(tmp, local, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        if (PreserveTimestamps) File.SetLastWriteTimeUtc(local, attrs.LastWriteTimeUtc);
        p.Report((ulong)attrs.Size);
        item.FilesDone++;
    }

    private async Task UploadDirectoryAsync(SftpClient c, string localDir, string remoteDir, TransferItem item, ProgressTracker p)
    {
        var ct = item.Cts.Token;
        var files = Directory.EnumerateFiles(localDir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        item.FilesTotal = files.Count;
        item.TotalBytes = files.Sum(f => new FileInfo(f).Length);
        await EnsureRemoteDirAsync(c, remoteDir, ct).ConfigureAwait(false);
        foreach (var d in Directory.EnumerateDirectories(localDir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            await EnsureRemoteDirAsync(c, SftpService.Combine(remoteDir, Path.GetRelativePath(localDir, d).Replace('\\', '/')), ct).ConfigureAwait(false);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(localDir, f).Replace('\\', '/');
            await UploadFileAsync(c, f, SftpService.Combine(remoteDir, rel), item, p).ConfigureAwait(false);
        }
    }

    private async Task DownloadDirectoryAsync(SftpClient c, string remoteDir, string localDir, TransferItem item, ProgressTracker p)
    {
        var ct = item.Cts.Token;
        var files = new List<(string Remote, string Rel, long Size)>();
        async Task Walk(string dir, string rel)
        {
            await foreach (var f in c.ListDirectoryAsync(dir, ct).ConfigureAwait(false))
            {
                if (f.Name is "." or "..") continue;
                var r = rel.Length == 0 ? f.Name : rel + "/" + f.Name;
                if (f.IsDirectory && !f.IsSymbolicLink) await Walk(f.FullName, r).ConfigureAwait(false);
                else if (f.IsRegularFile) files.Add((f.FullName, r, f.Length));
            }
        }
        await Walk(remoteDir, string.Empty).ConfigureAwait(false);
        item.FilesTotal = files.Count;
        item.TotalBytes = files.Sum(f => f.Size);
        Directory.CreateDirectory(localDir);
        foreach (var (remote, rel, _) in files)
        {
            ct.ThrowIfCancellationRequested();
            var local = Path.Combine(localDir, rel.Replace('/', Path.DirectorySeparatorChar));
            // Guard against path traversal from hostile file names.
            if (!Path.GetFullPath(local).StartsWith(Path.GetFullPath(localDir), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Refusing to write outside the target folder: {rel}");
            await DownloadFileAsync(c, remote, local, item, p).ConfigureAwait(false);
        }
    }

    private static async Task EnsureRemoteDirAsync(SftpClient c, string dir, CancellationToken ct)
    {
        if (await c.ExistsAsync(dir, ct).ConfigureAwait(false)) return;
        var parent = SftpService.Parent(dir);
        if (parent != dir && parent != "/") await EnsureRemoteDirAsync(c, parent, ct).ConfigureAwait(false);
        await c.CreateDirectoryAsync(dir, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ResolveConflictAsync(SftpClient c, string remote, DateTime localMtimeUtc, TransferItem item, CancellationToken ct)
    {
        if (!await c.ExistsAsync(remote, ct).ConfigureAwait(false)) return remote;
        switch (item.Conflict)
        {
            case ConflictPolicy.Skip: return null;
            case ConflictPolicy.OverwriteIfNewer:
                var attrs = await c.GetAttributesAsync(remote, ct).ConfigureAwait(false);
                return localMtimeUtc > attrs.LastWriteTimeUtc ? remote : null;
            case ConflictPolicy.Rename:
                var dir = SftpService.Parent(remote);
                var name = Path.GetFileNameWithoutExtension(remote);
                var ext = Path.GetExtension(remote);
                for (int i = 1; ; i++)
                {
                    var candidate = SftpService.Combine(dir, $"{name} ({i}){ext}");
                    if (!await c.ExistsAsync(candidate, ct).ConfigureAwait(false)) return candidate;
                }
            default: return remote;
        }
    }

    private static string UniqueLocal(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public ValueTask DisposeAsync()
    {
        List<SftpClient> all;
        lock (_lock)
        {
            _disposed = true;
            foreach (var i in _items) i.Cts.Cancel();
            all = [.. _all];
            _all.Clear();
            _idle.Clear();
        }
        foreach (var c in all)
        {
            try { c.Dispose(); } catch (Exception) { }
        }
        return ValueTask.CompletedTask;
    }
}
