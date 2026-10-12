using SessionForge.Core.Models;

namespace SessionForge.Core.Storage;

/// <summary>
/// In-memory session tree backed by a JSON file. All mutations persist immediately and raise <see cref="Changed"/>.
/// Secrets are never stored here — only references into the vault.
/// </summary>
public sealed class SessionStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private SessionDocument _doc;
    private const int MaxRecent = 15;

    public SessionStore(string path)
    {
        _path = path;
        _doc = JsonFile.Load<SessionDocument>(path);
        Normalize();
    }

    public event Action? Changed;

    public IReadOnlyList<Session> Sessions { get { lock (_lock) return _doc.Sessions.ToList(); } }
    public IReadOnlyList<SessionFolder> Folders { get { lock (_lock) return _doc.Folders.ToList(); } }

    public IReadOnlyList<Session> Recent
    {
        get
        {
            lock (_lock)
                return _doc.RecentSessionIds.Select(id => _doc.Sessions.FirstOrDefault(s => s.Id == id)).OfType<Session>().ToList();
        }
    }

    public Session? Get(Guid id) { lock (_lock) return _doc.Sessions.FirstOrDefault(s => s.Id == id); }
    public SessionFolder? GetFolder(Guid id) { lock (_lock) return _doc.Folders.FirstOrDefault(f => f.Id == id); }

    private void Normalize()
    {
        // Drop dangling folder references so sessions never disappear from the tree.
        var folderIds = _doc.Folders.Select(f => f.Id).ToHashSet();
        foreach (var f in _doc.Folders.Where(f => f.ParentId is { } p && (!folderIds.Contains(p) || p == f.Id))) f.ParentId = null;
        foreach (var s in _doc.Sessions.Where(s => s.FolderId is { } f && !folderIds.Contains(f))) s.FolderId = null;
        // Break folder cycles.
        foreach (var f in _doc.Folders)
        {
            var seen = new HashSet<Guid> { f.Id };
            var cur = f;
            while (cur.ParentId is { } pid)
            {
                if (!seen.Add(pid)) { f.ParentId = null; break; }
                cur = _doc.Folders.First(x => x.Id == pid);
            }
        }
        _doc.RecentSessionIds = _doc.RecentSessionIds.Distinct().Where(id => _doc.Sessions.Any(s => s.Id == id)).Take(MaxRecent).ToList();
    }

    private void Commit()
    {
        lock (_lock) JsonFile.Save(_path, _doc);
        Changed?.Invoke();
    }

    public void Save() => Commit();

    public Session AddOrUpdate(Session session)
    {
        lock (_lock)
        {
            int idx = _doc.Sessions.FindIndex(s => s.Id == session.Id);
            if (idx >= 0) _doc.Sessions[idx] = session;
            else
            {
                session.SortOrder = _doc.Sessions.Count == 0 ? 0 : _doc.Sessions.Max(s => s.SortOrder) + 1;
                _doc.Sessions.Add(session);
            }
        }
        Commit();
        return session;
    }

    public void Remove(Guid sessionId)
    {
        lock (_lock)
        {
            _doc.Sessions.RemoveAll(s => s.Id == sessionId);
            _doc.RecentSessionIds.Remove(sessionId);
            // Jump host references to the removed session become inline-less; keep them but they will fail validation.
        }
        Commit();
    }

    public Session Duplicate(Guid sessionId)
    {
        Session copy;
        lock (_lock)
        {
            var src = _doc.Sessions.First(s => s.Id == sessionId);
            copy = JsonFile.Clone(src);
            copy.Id = Guid.NewGuid();
            copy.Name = UniqueName(src.Name + " (copy)");
            copy.Created = DateTimeOffset.UtcNow;
            copy.LastConnected = null;
            copy.Credential.SavePassword = false; // secrets are keyed per session; don't silently share
            foreach (var f in copy.Ssh.Forwards) f.Id = Guid.NewGuid();
        }
        return AddOrUpdate(copy);
    }

    private string UniqueName(string name)
    {
        if (!_doc.Sessions.Any(s => s.Name == name)) return name;
        for (int i = 2; ; i++)
        {
            var n = $"{name} {i}";
            if (!_doc.Sessions.Any(s => s.Name == n)) return n;
        }
    }

    public void MoveSession(Guid sessionId, Guid? folderId)
    {
        lock (_lock)
        {
            var s = _doc.Sessions.First(x => x.Id == sessionId);
            if (folderId is { } f && _doc.Folders.All(x => x.Id != f)) throw new ArgumentException("Folder does not exist.", nameof(folderId));
            s.FolderId = folderId;
        }
        Commit();
    }

    public SessionFolder AddFolder(string name, Guid? parentId)
    {
        var folder = new SessionFolder { Name = name, ParentId = parentId };
        lock (_lock)
        {
            if (parentId is { } p && _doc.Folders.All(x => x.Id != p)) throw new ArgumentException("Parent folder does not exist.", nameof(parentId));
            _doc.Folders.Add(folder);
        }
        Commit();
        return folder;
    }

    public void RenameFolder(Guid folderId, string name)
    {
        lock (_lock) _doc.Folders.First(f => f.Id == folderId).Name = name;
        Commit();
    }

    public void SetFolderExpanded(Guid folderId, bool expanded)
    {
        lock (_lock)
        {
            var f = _doc.Folders.FirstOrDefault(x => x.Id == folderId);
            if (f == null || f.Expanded == expanded) return;
            f.Expanded = expanded;
        }
        Commit();
    }

    public void MoveFolder(Guid folderId, Guid? newParentId)
    {
        lock (_lock)
        {
            if (newParentId == folderId) throw new InvalidOperationException("A folder cannot contain itself.");
            // Refuse to move a folder into its own descendant.
            for (var cur = newParentId; cur is { } c; cur = _doc.Folders.First(x => x.Id == c).ParentId)
                if (c == folderId) throw new InvalidOperationException("A folder cannot be moved into its own subfolder.");
            _doc.Folders.First(f => f.Id == folderId).ParentId = newParentId;
        }
        Commit();
    }

    /// <summary>Deletes a folder. Contents are either deleted or moved up to the parent folder.</summary>
    public void DeleteFolder(Guid folderId, bool deleteContents)
    {
        lock (_lock)
        {
            var folder = _doc.Folders.First(f => f.Id == folderId);
            var descendants = Descendants(folderId).ToHashSet();
            descendants.Add(folderId);
            if (deleteContents)
            {
                var removed = _doc.Sessions.Where(s => s.FolderId is { } f && descendants.Contains(f)).Select(s => s.Id).ToHashSet();
                _doc.Sessions.RemoveAll(s => removed.Contains(s.Id));
                _doc.RecentSessionIds.RemoveAll(removed.Contains);
                _doc.Folders.RemoveAll(f => descendants.Contains(f.Id));
            }
            else
            {
                foreach (var s in _doc.Sessions.Where(s => s.FolderId == folderId)) s.FolderId = folder.ParentId;
                foreach (var f in _doc.Folders.Where(f => f.ParentId == folderId)) f.ParentId = folder.ParentId;
                _doc.Folders.Remove(folder);
            }
        }
        Commit();
    }

    private IEnumerable<Guid> Descendants(Guid folderId)
    {
        foreach (var child in _doc.Folders.Where(f => f.ParentId == folderId))
        {
            yield return child.Id;
            foreach (var d in Descendants(child.Id)) yield return d;
        }
    }

    public string FolderPath(Guid? folderId)
    {
        lock (_lock)
        {
            var parts = new List<string>();
            var seen = new HashSet<Guid>();
            for (var cur = folderId; cur is { } c && seen.Add(c);)
            {
                var f = _doc.Folders.FirstOrDefault(x => x.Id == c);
                if (f == null) break;
                parts.Insert(0, f.Name);
                cur = f.ParentId;
            }
            return string.Join(" / ", parts);
        }
    }

    public void MarkConnected(Guid sessionId)
    {
        lock (_lock)
        {
            var s = _doc.Sessions.FirstOrDefault(x => x.Id == sessionId);
            if (s == null) return;
            s.LastConnected = DateTimeOffset.UtcNow;
            _doc.RecentSessionIds.Remove(sessionId);
            _doc.RecentSessionIds.Insert(0, sessionId);
            if (_doc.RecentSessionIds.Count > MaxRecent) _doc.RecentSessionIds.RemoveRange(MaxRecent, _doc.RecentSessionIds.Count - MaxRecent);
        }
        Commit();
    }

    /// <summary>Case-insensitive search over name, host, user, notes, tags and folder path.</summary>
    public IReadOnlyList<Session> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Sessions;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_lock)
        {
            return _doc.Sessions.Where(s =>
            {
                var hay = string.Join('\n', s.Name, s.Host, s.Credential.Username ?? "", s.Notes ?? "", string.Join(' ', s.Tags), s.Kind.ToString(), FolderPathUnlocked(s.FolderId));
                return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
            }).ToList();
        }
    }

    private string FolderPathUnlocked(Guid? folderId)
    {
        var parts = new List<string>();
        var seen = new HashSet<Guid>();
        for (var cur = folderId; cur is { } c && seen.Add(c);)
        {
            var f = _doc.Folders.FirstOrDefault(x => x.Id == c);
            if (f == null) break;
            parts.Insert(0, f.Name);
            cur = f.ParentId;
        }
        return string.Join(" / ", parts);
    }

    /// <summary>Exports selected sessions (or all) with their folders. Secrets are never included.</summary>
    public string Export(IEnumerable<Guid>? sessionIds = null)
    {
        lock (_lock)
        {
            var set = sessionIds?.ToHashSet();
            var sessions = _doc.Sessions.Where(s => set == null || set.Contains(s.Id)).Select(JsonFile.Clone).ToList();
            foreach (var s in sessions) s.Credential.SavePassword = false;
            var neededFolders = new HashSet<Guid>();
            foreach (var s in sessions)
                for (var cur = s.FolderId; cur is { } c && neededFolders.Add(c);)
                    cur = _doc.Folders.FirstOrDefault(f => f.Id == c)?.ParentId;
            var doc = new SessionDocument
            {
                Sessions = sessions,
                Folders = _doc.Folders.Where(f => set == null || neededFolders.Contains(f.Id)).Select(JsonFile.Clone).ToList(),
            };
            return JsonFile.Serialize(doc);
        }
    }

    /// <summary>Imports an exported document under an optional target folder. Ids are regenerated to avoid collisions.</summary>
    public int Import(string json, Guid? targetFolder = null)
    {
        var doc = JsonFile.Deserialize<SessionDocument>(json) ?? throw new InvalidDataException("Not a SessionForge session export.");
        int count;
        lock (_lock)
        {
            var map = doc.Folders.ToDictionary(f => f.Id, _ => Guid.NewGuid());
            foreach (var f in doc.Folders)
            {
                f.Id = map[f.Id];
                f.ParentId = f.ParentId is { } p && map.TryGetValue(p, out var np) ? np : targetFolder;
                _doc.Folders.Add(f);
            }
            var sessionMap = doc.Sessions.ToDictionary(s => s.Id, _ => Guid.NewGuid());
            foreach (var s in doc.Sessions)
            {
                s.Id = sessionMap[s.Id];
                s.FolderId = s.FolderId is { } f && map.TryGetValue(f, out var nf) ? nf : targetFolder;
                s.Credential.SavePassword = false;
                foreach (var j in s.Ssh.JumpHosts)
                    if (j.SessionId is { } jid) j.SessionId = sessionMap.TryGetValue(jid, out var njid) ? njid : jid;
                _doc.Sessions.Add(s);
            }
            count = doc.Sessions.Count;
            Normalize();
        }
        Commit();
        return count;
    }
}
