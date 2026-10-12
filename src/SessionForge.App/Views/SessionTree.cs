using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SessionForge.App.Services;
using SessionForge.Core.Models;

namespace SessionForge.App.Views;

public abstract partial class TreeNode : ObservableObject
{
    public ObservableCollection<TreeNode> Children { get; } = [];
    public abstract string Name { get; }
    public abstract string Glyph { get; }
    public virtual string? Subtitle => null;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    public FolderNode? ParentFolder { get; set; }
}

public sealed partial class FolderNode : TreeNode
{
    public FolderNode(SessionFolder folder) { Folder = folder; IsExpanded = folder.Expanded; }
    public SessionFolder Folder { get; }
    public override string Name => Folder.Name;
    public override string Glyph => IsExpanded ? "" : "";
    public override string? Subtitle => null;
    public int Count => Children.Sum(c => c is FolderNode f ? f.Count : 1);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsExpanded)) return;
        OnPropertyChanged(nameof(Glyph));
        if (Folder.Expanded != IsExpanded) AppServices.Sessions.SetFolderExpanded(Folder.Id, IsExpanded);
    }
}

public sealed partial class SessionNode : TreeNode
{
    public SessionNode(Session session) => Session = session;
    public Session Session { get; }
    public override string Name => Session.Name;
    public override string Glyph => GlyphFor(Session.Kind);
    public override string? Subtitle => Session.Describe();
    [ObservableProperty] private int _openCount;
    public string? Color => Session.TabColor;

    public static string GlyphFor(SessionKind k) => k switch
    {
        SessionKind.Ssh => "",
        SessionKind.Sftp => "",
        SessionKind.Rdp => "",
        SessionKind.Vnc => "",
        SessionKind.Telnet => "",
        SessionKind.Serial => "",
        SessionKind.LocalShell => "",
        _ => "",
    };
}

/// <summary>Builds the sidebar tree from the session store (folders first, then sessions, alphabetical).</summary>
public static class SessionTreeBuilder
{
    public static List<TreeNode> Build(string? filter, IReadOnlyDictionary<Guid, int> openCounts)
    {
        var store = AppServices.Sessions;
        var sessions = string.IsNullOrWhiteSpace(filter) ? store.Sessions : store.Search(filter);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            // Flat result list while filtering.
            return sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => (TreeNode)new SessionNode(s) { OpenCount = openCounts.GetValueOrDefault(s.Id) }).ToList();
        }

        var folders = store.Folders.ToDictionary(f => f.Id, f => new FolderNode(f));
        var roots = new List<TreeNode>();
        foreach (var f in folders.Values.OrderBy(f => f.Folder.SortOrder).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (f.Folder.ParentId is { } p && folders.TryGetValue(p, out var parent)) { parent.Children.Add(f); f.ParentFolder = parent; }
            else roots.Add(f);
        }
        foreach (var s in sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = new SessionNode(s) { OpenCount = openCounts.GetValueOrDefault(s.Id) };
            if (s.FolderId is { } fid && folders.TryGetValue(fid, out var folder)) { folder.Children.Add(node); node.ParentFolder = folder; }
            else roots.Add(node);
        }
        // Folders before sessions at each level.
        static void Sort(ObservableCollection<TreeNode> list)
        {
            var sorted = list.OrderBy(n => n is FolderNode ? 0 : 1).ToList();
            list.Clear();
            foreach (var n in sorted) { list.Add(n); if (n is FolderNode f) Sort(f.Children); }
        }
        var rootCollection = new ObservableCollection<TreeNode>(roots);
        Sort(rootCollection);
        return rootCollection.ToList();
    }
}
