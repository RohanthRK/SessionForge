using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace SessionForge.Protocols.Sftp;

public sealed record RemoteEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTime ModifiedUtc,
    string Permissions,
    int Mode,
    int UserId,
    int GroupId)
{
    public bool IsParentLink => Name == "..";
}

/// <summary>Remote file system operations over SFTP (browsing client). Thread-safe for sequential callers.</summary>
public sealed class SftpService : IDisposable
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SftpService(SftpClient client) => _client = client;

    public bool IsConnected => _client.IsConnected;
    public string WorkingDirectory => _client.WorkingDirectory;

    public static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string Parent(string path)
    {
        if (path is "/" or "") return "/";
        var p = path.TrimEnd('/');
        int i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    private async Task<T> Locked<T>(Func<Task<T>> f)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await f().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task Locked(Func<Task> f)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await f().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task<string> GetHomeAsync() => Task.FromResult(_client.WorkingDirectory);

    public Task<string> ResolveAsync(string path, CancellationToken ct) => Locked(async () =>
    {
        var attrs = await _client.GetAsync(path, ct).ConfigureAwait(false);
        return attrs.FullName;
    });

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Locked<IReadOnlyList<RemoteEntry>>(async () =>
    {
        var list = new List<RemoteEntry>();
        await foreach (var f in _client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (f.Name == ".") continue;
            bool isDir = f.IsDirectory;
            if (f.IsSymbolicLink)
            {
                // Resolve link targets so directories behind links are browsable.
                try { isDir = _client.GetAttributes(f.FullName).IsDirectory; }
                catch (SftpPathNotFoundException) { }
                catch (SshException) { }
            }
            list.Add(ToEntry(f, isDir));
        }
        return list
            .OrderByDescending(e => e.IsParentLink)
            .ThenByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    private static RemoteEntry ToEntry(ISftpFile f, bool isDir) => new(
        f.Name, f.FullName, isDir, f.IsSymbolicLink, isDir ? 0 : f.Length, f.LastWriteTimeUtc,
        PermissionString(f), ModeOf(f), f.UserId, f.GroupId);

    public static int ModeOf(ISftpFile f)
    {
        int m = 0;
        if (f.OwnerCanRead) m |= 0x100; if (f.OwnerCanWrite) m |= 0x80; if (f.OwnerCanExecute) m |= 0x40;
        if (f.GroupCanRead) m |= 0x20; if (f.GroupCanWrite) m |= 0x10; if (f.GroupCanExecute) m |= 0x8;
        if (f.OthersCanRead) m |= 0x4; if (f.OthersCanWrite) m |= 0x2; if (f.OthersCanExecute) m |= 0x1;
        return m;
    }

    public static string PermissionString(ISftpFile f)
    {
        var sb = new StringBuilder(10);
        sb.Append(f.IsDirectory ? 'd' : f.IsSymbolicLink ? 'l' : '-');
        sb.Append(f.OwnerCanRead ? 'r' : '-').Append(f.OwnerCanWrite ? 'w' : '-').Append(f.OwnerCanExecute ? 'x' : '-');
        sb.Append(f.GroupCanRead ? 'r' : '-').Append(f.GroupCanWrite ? 'w' : '-').Append(f.GroupCanExecute ? 'x' : '-');
        sb.Append(f.OthersCanRead ? 'r' : '-').Append(f.OthersCanWrite ? 'w' : '-').Append(f.OthersCanExecute ? 'x' : '-');
        return sb.ToString();
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Locked(() => _client.CreateDirectoryAsync(path, ct));

    public Task CreateEmptyFileAsync(string path, CancellationToken ct) => Locked(async () =>
    {
        if (await _client.ExistsAsync(path, ct).ConfigureAwait(false)) throw new IOException($"'{path}' already exists.");
        using var s = await _client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false);
    });

    public Task RenameAsync(string from, string to, CancellationToken ct) => Locked(() => _client.RenameFileAsync(from, to, ct));

    public Task ChmodAsync(string path, int mode) => Locked(() =>
    {
        _client.ChangePermissions(path, ToSshNetMode(mode));
        return Task.CompletedTask;
    });

    /// <summary>SSH.NET expects the mode written with octal digits as a decimal number (0o755 → 755).</summary>
    public static short ToSshNetMode(int permissionBits) =>
        short.Parse(Convert.ToString(permissionBits & 0x1FF, 8), System.Globalization.CultureInfo.InvariantCulture);

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Locked(() => _client.ExistsAsync(path, ct));

    /// <summary>Deletes a file, or a directory recursively.</summary>
    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct) => Locked(() => DeleteUnlockedAsync(entry.FullPath, entry.IsDirectory && !entry.IsSymlink, ct));

    private async Task DeleteUnlockedAsync(string path, bool isDir, CancellationToken ct)
    {
        if (!isDir) { await _client.DeleteFileAsync(path, ct).ConfigureAwait(false); return; }
        var children = new List<ISftpFile>();
        await foreach (var f in _client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
            if (f.Name is not "." and not "..") children.Add(f);
        foreach (var c in children)
        {
            ct.ThrowIfCancellationRequested();
            await DeleteUnlockedAsync(c.FullName, c.IsDirectory && !c.IsSymbolicLink, ct).ConfigureAwait(false);
        }
        await _client.DeleteDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a (small) remote file fully, e.g. for the built-in editor. Refuses files above <paramref name="maxBytes"/>.</summary>
    public Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken ct) => Locked(async () =>
    {
        var attrs = await _client.GetAttributesAsync(path, ct).ConfigureAwait(false);
        if (attrs.Size > maxBytes) throw new IOException($"File is too large to open in the editor ({attrs.Size:N0} bytes).");
        using var ms = new MemoryStream();
        await _client.DownloadFileAsync(path, ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    });

    /// <summary>Writes a remote file safely: upload to a temp name, then rename over the original (keeps permissions).</summary>
    public Task WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => Locked(async () =>
    {
        var tmp = path + ".sf-tmp-" + Guid.NewGuid().ToString("N")[..8];
        int? mode = null;
        try { mode = ModeOf(await _client.GetAsync(path, ct).ConfigureAwait(false)); } catch (SftpPathNotFoundException) { }
        using (var ms = new MemoryStream(data))
            await _client.UploadFileAsync(ms, tmp, ct).ConfigureAwait(false);
        if (mode is { } m) _client.ChangePermissions(tmp, ToSshNetMode(m));
        try
        {
            // posix-rename@openssh.com overwrites atomically when supported.
            _client.RenameFile(tmp, path, isPosix: true);
        }
        catch (SshException)
        {
            await _client.DeleteFileAsync(path, ct).ConfigureAwait(false);
            await _client.RenameFileAsync(tmp, path, ct).ConfigureAwait(false);
        }
    });

    public void Dispose()
    {
        try { if (_client.IsConnected) _client.Disconnect(); } catch (Exception) { }
        _client.Dispose();
        _gate.Dispose();
    }
}
