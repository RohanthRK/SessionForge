using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SessionForge.App.Workspace;

public abstract class PaneNode
{
    public SplitNode? Parent { get; set; }
}

public sealed class LeafNode : PaneNode
{
    public LeafNode(SessionHost host) { Host = host; Frame = new PaneFrame(this); }
    public SessionHost Host { get; }
    public PaneFrame Frame { get; }
}

public sealed class SplitNode : PaneNode
{
    public required Orientation Orientation { get; init; }
    public required PaneNode First { get; set; }
    public required PaneNode Second { get; set; }
    public double Ratio { get; set; } = 0.5;
}

/// <summary>Border around a pane: shows a slim header (title + close) when the tab has several panes and highlights the active one.</summary>
public sealed class PaneFrame : Border
{
    private readonly DockPanel _dock = new();
    private readonly Border _header = new() { Height = 24, BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _content = new() { Focusable = false };

    public PaneFrame(LeafNode leaf)
    {
        Leaf = leaf;
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        var headerDock = new DockPanel();
        var close = new Button { Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9 }, ToolTip = "Close pane", Padding = new Thickness(6, 2, 6, 2), MinHeight = 20, MinWidth = 22 };
        close.SetResourceReference(StyleProperty, "Button.Ghost");
        close.Click += (_, _) => CloseRequested?.Invoke(Leaf);
        DockPanel.SetDock(close, Dock.Right);
        headerDock.Children.Add(close);
        headerDock.Children.Add(_dot);
        headerDock.Children.Add(_title);
        _header.Child = headerDock;
        _header.SetResourceReference(Border.BackgroundProperty, "Bg.Chrome");
        _header.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        DockPanel.SetDock(_header, Dock.Top);
        _dock.Children.Add(_header);
        _dock.Children.Add(_content);
        Child = _dock;
        _content.Content = leaf.Host.View;
        leaf.Host.TitleChanged += Update;
        leaf.Host.StateChanged += Update;
        PreviewMouseDown += (_, _) => Activated?.Invoke(Leaf);
        PreviewGotKeyboardFocus += (_, _) => Activated?.Invoke(Leaf);
        Update();
    }

    public LeafNode Leaf { get; }
    public event Action<LeafNode>? Activated;
    public event Action<LeafNode>? CloseRequested;

    public bool ShowHeader { set => _header.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public bool IsActivePane
    {
        set
        {
            if (value) SetResourceReference(BorderBrushProperty, "Accent");
            else BorderBrush = Brushes.Transparent;
        }
    }

    public bool Broadcasting
    {
        set
        {
            if (value) SetResourceReference(BorderBrushProperty, "Warning");
        }
    }

    private void Update()
    {
        _title.Text = Leaf.Host.Title;
        _dot.SetResourceReference(Shape.FillProperty, Leaf.Host.State switch
        {
            HostState.Connected => "Success",
            HostState.Connecting => "Warning",
            HostState.Failed => "Danger",
            _ => "Fg.Muted",
        });
    }
}

/// <summary>A top-level tab: a tree of split panes, each hosting a session.</summary>
public sealed partial class WorkspaceTab : ObservableObject
{
    private readonly Grid _container = new();
    private PaneNode _root;
    private LeafNode _active;

    public WorkspaceTab(SessionHost host)
    {
        var leaf = new LeafNode(host);
        _root = leaf;
        _active = leaf;
        Hook(leaf);
        Rebuild();
        UpdateHeader();
    }

    public FrameworkElement Content => _container;
    public LeafNode ActiveLeaf => _active;
    public SessionHost ActiveHost => _active.Host;
    public IEnumerable<LeafNode> Leaves => Enumerate(_root);
    public int PaneCount => Leaves.Count();

    [ObservableProperty] private string _header = string.Empty;
    [ObservableProperty] private string? _tabColor;
    [ObservableProperty] private HostState _state;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string? _customTitle;

    public event Action<WorkspaceTab>? ActivePaneChanged;
    public event Action<WorkspaceTab, LeafNode>? PaneClosing;
    public event Action<WorkspaceTab>? Emptied;

    partial void OnCustomTitleChanged(string? value) => UpdateHeader();

    private static IEnumerable<LeafNode> Enumerate(PaneNode n)
    {
        if (n is LeafNode l) { yield return l; yield break; }
        var s = (SplitNode)n;
        foreach (var x in Enumerate(s.First)) yield return x;
        foreach (var x in Enumerate(s.Second)) yield return x;
    }

    private void Hook(LeafNode leaf)
    {
        leaf.Frame.Activated += SetActive;
        leaf.Frame.CloseRequested += l => PaneClosing?.Invoke(this, l);
        leaf.Host.TitleChanged += UpdateHeader;
        leaf.Host.StateChanged += UpdateHeader;
    }

    public void SetActive(LeafNode leaf)
    {
        if (_active == leaf) return;
        _active = leaf;
        UpdateFrames();
        UpdateHeader();
        ActivePaneChanged?.Invoke(this);
    }

    private void UpdateHeader()
    {
        var leaves = Leaves.ToList();
        Header = CustomTitle ?? (leaves.Count > 1 ? $"{_active.Host.Title}  (+{leaves.Count - 1})" : _active.Host.Title);
        State = _active.Host.State;
        TabColor = _active.Host.Session.TabColor;
    }

    public bool MultiExecHighlight { get; set; }

    private void UpdateFrames()
    {
        var leaves = Leaves.ToList();
        foreach (var l in leaves)
        {
            l.Frame.ShowHeader = leaves.Count > 1;
            l.Frame.IsActivePane = leaves.Count > 1 && l == _active;
            if (MultiExecHighlight && l.Host.IsTerminal) l.Frame.Broadcasting = true;
        }
    }

    public void RefreshFrames() => UpdateFrames();

    /// <summary>Splits the active pane, placing <paramref name="host"/> beside (vertical split) or below (horizontal split) it.</summary>
    public LeafNode Split(SessionHost host, Orientation orientation)
    {
        var newLeaf = new LeafNode(host);
        Hook(newLeaf);
        var target = _active;
        var split = new SplitNode { Orientation = orientation, First = target, Second = newLeaf, Parent = target.Parent };
        if (target.Parent == null) _root = split;
        else if (target.Parent.First == target) target.Parent.First = split;
        else target.Parent.Second = split;
        target.Parent = split;
        newLeaf.Parent = split;
        _active = newLeaf;
        Rebuild();
        UpdateHeader();
        ActivePaneChanged?.Invoke(this);
        return newLeaf;
    }

    /// <summary>Removes a pane from the layout (the caller disposes the host). Raises <see cref="Emptied"/> when no panes remain.</summary>
    public void Remove(LeafNode leaf)
    {
        if (_root == leaf)
        {
            Detach(_root);
            Emptied?.Invoke(this);
            return;
        }
        var parent = leaf.Parent!;
        var sibling = parent.First == leaf ? parent.Second : parent.First;
        sibling.Parent = parent.Parent;
        if (parent.Parent == null) _root = sibling;
        else if (parent.Parent.First == parent) parent.Parent.First = sibling;
        else parent.Parent.Second = sibling;
        if (_active == leaf) _active = Enumerate(sibling).First();
        Rebuild();
        UpdateHeader();
        ActivePaneChanged?.Invoke(this);
    }

    /// <summary>Changes split orientation for the active pane's parent (used by "rotate split").</summary>
    public void RotateActiveSplit()
    {
        if (_active.Parent is not { } p) return;
        var replacement = new SplitNode { Orientation = p.Orientation == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal, First = p.First, Second = p.Second, Parent = p.Parent, Ratio = p.Ratio };
        p.First.Parent = replacement;
        p.Second.Parent = replacement;
        if (p.Parent == null) _root = replacement;
        else if (p.Parent.First == p) p.Parent.First = replacement;
        else p.Parent.Second = replacement;
        Rebuild();
    }

    private void Detach(PaneNode n)
    {
        foreach (var l in Enumerate(n))
            if (l.Frame.Parent is Panel panel) panel.Children.Remove(l.Frame);
    }

    private void Rebuild()
    {
        // Detach frames from their current panels before re-parenting them.
        foreach (var l in Leaves)
            if (l.Frame.Parent is Panel p) p.Children.Remove(l.Frame);
        _container.Children.Clear();
        _container.Children.Add(Build(_root));
        UpdateFrames();
    }

    private FrameworkElement Build(PaneNode n)
    {
        if (n is LeafNode l) return l.Frame;
        var s = (SplitNode)n;
        var g = new Grid();
        var a = Build(s.First);
        var b = Build(s.Second);
        var splitter = new GridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
        splitter.SetResourceReference(Control.BackgroundProperty, "Border.Default");
        if (s.Orientation == Orientation.Vertical)
        {
            // Side by side
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Ratio, GridUnitType.Star), MinWidth = 80 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - s.Ratio, GridUnitType.Star), MinWidth = 80 });
            Grid.SetColumn(a, 0); Grid.SetColumn(splitter, 1); Grid.SetColumn(b, 2);
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.Cursor = Cursors.SizeWE;
            splitter.DragCompleted += (_, _) => s.Ratio = g.ColumnDefinitions[0].ActualWidth / Math.Max(1, g.ColumnDefinitions[0].ActualWidth + g.ColumnDefinitions[2].ActualWidth);
        }
        else
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(s.Ratio, GridUnitType.Star), MinHeight = 60 });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - s.Ratio, GridUnitType.Star), MinHeight = 60 });
            Grid.SetRow(a, 0); Grid.SetRow(splitter, 1); Grid.SetRow(b, 2);
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.Cursor = Cursors.SizeNS;
            splitter.DragCompleted += (_, _) => s.Ratio = g.RowDefinitions[0].ActualHeight / Math.Max(1, g.RowDefinitions[0].ActualHeight + g.RowDefinitions[2].ActualHeight);
        }
        g.Children.Add(a);
        g.Children.Add(splitter);
        g.Children.Add(b);
        return g;
    }

    /// <summary>Moves focus to the next/previous pane.</summary>
    public void CyclePane(int direction)
    {
        var leaves = Leaves.ToList();
        if (leaves.Count < 2) return;
        int i = leaves.IndexOf(_active);
        var next = leaves[(i + direction + leaves.Count) % leaves.Count];
        SetActive(next);
        next.Host.FocusView();
    }
}
