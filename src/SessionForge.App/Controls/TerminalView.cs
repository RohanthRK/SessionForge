using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SessionForge.Terminal;

namespace SessionForge.App.Controls;

/// <summary>Terminal surface + scrollbar + find bar + status overlay.</summary>
public sealed class TerminalView : Grid
{
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical };
    private readonly Border _findBar;
    private readonly TextBox _findBox = new() { Width = 220, Tag = "Find (Enter / Shift+Enter)" };
    private readonly CheckBox _findCase = new() { Content = "Aa", ToolTip = "Match case", Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _findRegex = new() { Content = ".*", ToolTip = "Regular expression", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _findStatus = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 70 };
    private readonly Border _overlay;
    private readonly TextBlock _overlayText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 520 };
    private readonly StackPanel _overlayButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private bool _updatingScroll;

    private readonly Border _monitorBar;
    private readonly TextBlock _monitorText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public TerminalView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Remote monitoring strip (hidden unless enabled)
        _monitorText.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        _monitorBar = new Border { Child = _monitorText, Padding = new Thickness(10, 3, 10, 3), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };
        _monitorBar.SetResourceReference(Border.BackgroundProperty, "Bg.Chrome");
        _monitorBar.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        SetRow(_monitorBar, 1);
        SetColumnSpan(_monitorBar, 2);
        Children.Add(_monitorBar);

        Terminal = new TerminalControl();
        Children.Add(Terminal);

        SetColumn(_scroll, 1);
        _scroll.SmallChange = 1;
        _scroll.Scroll += (_, _) => { if (!_updatingScroll) Terminal.ScrollTo((int)Math.Round(_scroll.Value)); };
        Children.Add(_scroll);
        Terminal.ViewportChanged += UpdateScrollBar;

        // Find bar (top-right overlay)
        var findPanel = new StackPanel { Orientation = Orientation.Horizontal };
        findPanel.Children.Add(_findBox);
        findPanel.Children.Add(_findCase);
        findPanel.Children.Add(_findRegex);
        findPanel.Children.Add(_findStatus);
        var prev = IconButton("", "Previous match (Shift+Enter)", () => DoFind(true));
        var next = IconButton("", "Next match (Enter)", () => DoFind(false));
        var close = IconButton("", "Close (Esc)", CloseFind);
        findPanel.Children.Add(prev);
        findPanel.Children.Add(next);
        findPanel.Children.Add(close);
        _findBar = new Border
        {
            Child = findPanel, Padding = new Thickness(8, 6, 6, 6), CornerRadius = new CornerRadius(0, 0, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 18, 0),
            Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1, 0, 1, 1),
        };
        _findBar.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
        _findBar.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        _findBar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3 };
        Children.Add(_findBar);
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DoFind((Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        _findBox.TextChanged += (_, _) => { Terminal.ClearSearch(); UpdateFindCount(); };
        _findCase.Click += (_, _) => UpdateFindCount();
        _findRegex.Click += (_, _) => UpdateFindCount();

        // Status overlay (disconnected / reconnect)
        var overlayContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        overlayContent.Children.Add(_overlayText);
        overlayContent.Children.Add(_overlayButtons);
        _overlay = new Border
        {
            Child = overlayContent, Padding = new Thickness(22, 16, 22, 16), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 20, 20, 28),
            Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1),
        };
        _overlay.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
        _overlay.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        _overlayText.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        _overlay.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 };
        Children.Add(_overlay);
    }

    public TerminalControl Terminal { get; }

    private static Button IconButton(string glyph, string tip, Action onClick)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 }, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "Button.Ghost");
        b.Click += (_, _) => onClick();
        return b;
    }

    private void UpdateScrollBar()
    {
        var emu = Terminal.Emulator;
        if (emu == null) return;
        _updatingScroll = true;
        int max;
        lock (emu.SyncRoot) max = emu.ScrollbackCount;
        _scroll.Minimum = 0;
        _scroll.Maximum = max;
        _scroll.ViewportSize = Math.Max(1, Terminal.Rows);
        _scroll.LargeChange = Math.Max(1, Terminal.Rows - 1);
        _scroll.Value = Terminal.ViewportTop;
        _scroll.Visibility = max > 0 ? Visibility.Visible : Visibility.Hidden;
        _updatingScroll = false;
    }

    public void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        var sel = Terminal.GetSelectedText();
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n')) _findBox.Text = sel;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    public void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        Terminal.ClearSearch();
        Terminal.Focus();
    }

    private SearchOptions Options(bool backward) => new(_findCase.IsChecked == true, _findRegex.IsChecked == true, backward);

    private void DoFind(bool backward)
    {
        bool found = Terminal.FindNext(_findBox.Text, Options(backward));
        if (!found && _findBox.Text.Length > 0) _findStatus.Text = "No results";
    }

    private void UpdateFindCount()
    {
        var emu = Terminal.Emulator;
        if (emu == null || _findBox.Text.Length == 0) { _findStatus.Text = string.Empty; return; }
        int n = emu.CountMatches(_findBox.Text, Options(false), 999);
        _findStatus.Text = n == 0 ? "No results" : n >= 999 ? "999+ matches" : $"{n} match{(n == 1 ? "" : "es")}";
        _findStatus.SetResourceReference(TextBlock.ForegroundProperty, n == 0 ? "Danger" : "Fg.Secondary");
    }

    /// <summary>Shows an overlay message with optional action buttons (e.g. Reconnect / Close).</summary>
    public void ShowOverlay(string message, params (string Label, Action Action, bool Primary)[] actions)
    {
        _overlayText.Text = message;
        _overlayButtons.Children.Clear();
        foreach (var (label, action, primary) in actions)
        {
            var b = new Button { Content = label, Margin = new Thickness(4, 0, 4, 0), MinWidth = 96 };
            b.SetResourceReference(StyleProperty, primary ? "Button.Primary" : typeof(Button));
            b.Click += (_, _) => action();
            _overlayButtons.Children.Add(b);
        }
        _overlayButtons.Visibility = actions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = Visibility.Visible;
    }

    public void HideOverlay() => _overlay.Visibility = Visibility.Collapsed;

    /// <summary>Shows (or hides, when null) the remote monitoring strip under the terminal.</summary>
    public void SetMonitorText(string? text)
    {
        _monitorBar.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        _monitorText.Text = text ?? string.Empty;
    }
    public bool OverlayVisible => _overlay.Visibility == Visibility.Visible;
}
