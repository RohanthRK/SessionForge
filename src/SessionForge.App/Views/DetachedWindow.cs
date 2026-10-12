using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SessionForge.App.Services;
using SessionForge.App.Workspace;

namespace SessionForge.App.Views;

/// <summary>A workspace tab shown in its own top-level window. Closing the window re-attaches the tab.</summary>
public sealed class DetachedWindow : Window
{
    private readonly ContentControl _host = new();
    private bool _reattaching;

    public DetachedWindow(WorkspaceTab tab, Action<WorkspaceTab> reattach)
    {
        Tab = tab;
        Title = tab.Header + " — SessionForge";
        Width = 1000;
        Height = 680;
        SetResourceReference(BackgroundProperty, "Bg.Window");
        SetResourceReference(ForegroundProperty, "Fg.Primary");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/SessionForge;component/Assets/SessionForge.ico"));
        ThemeManager.ApplyChrome(this);

        var root = new DockPanel();
        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 3, 6, 3) };
        bar.SetResourceReference(Border.BackgroundProperty, "Bg.Chrome");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var back = new Button { Content = "Re-attach to main window", ToolTip = "Move this tab back into the main window" };
        back.SetResourceReference(StyleProperty, "Button.Ghost");
        back.Click += (_, _) => Reattach(reattach);
        var top = new System.Windows.Controls.Primitives.ToggleButton { Content = "Always on top", ToolTip = "Keep this window above others", Margin = new Thickness(6, 0, 0, 0) };
        top.Click += (_, _) => Topmost = top.IsChecked == true;
        tools.Children.Add(back);
        tools.Children.Add(top);
        bar.Child = tools;
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_host);
        Content = root;

        _host.Content = tab.Content;
        tab.Content.Visibility = Visibility.Visible;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkspaceTab.Header)) Title = tab.Header + " — SessionForge"; };
        Closing += (_, _) => { if (!_reattaching) Reattach(reattach); };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Up) { tab.CyclePane(-1); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Right or Key.Down) { tab.CyclePane(1); e.Handled = true; }
        };
        Loaded += (_, _) => tab.ActiveHost.FocusView();
    }

    public WorkspaceTab Tab { get; }

    /// <summary>Closes the window without moving the tab back (its sessions were closed).</summary>
    public void CloseWithoutReattach()
    {
        _reattaching = true;
        _host.Content = null;
        Close();
    }

    private void Reattach(Action<WorkspaceTab> reattach)
    {
        if (_reattaching) return;
        _reattaching = true;
        _host.Content = null; // release the tab content so it can be re-parented
        reattach(Tab);
        if (IsVisible) Close();
    }
}
