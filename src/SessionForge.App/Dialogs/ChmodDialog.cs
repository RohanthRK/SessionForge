using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SessionForge.App.Dialogs;

/// <summary>Unix permission editor (rwx grid + octal).</summary>
public sealed class ChmodDialog : DialogBase
{
    private readonly CheckBox[] _bits = new CheckBox[9];
    private readonly TextBox _octal = new() { Width = 70, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private bool _sync;

    private ChmodDialog(string name, int mode)
    {
        Title = "Permissions";
        Width = 360;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("Text.Title"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 14) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] cols = ["Read", "Write", "Execute"], rows = ["Owner", "Group", "Others"];
        for (int c = 0; c < 3; c++) Add(grid, new TextBlock { Text = cols[c], Foreground = (System.Windows.Media.Brush)FindResource("Fg.Muted") }, 0, c + 1);
        for (int r = 0; r < 3; r++)
        {
            Add(grid, new TextBlock { Text = rows[r], VerticalAlignment = VerticalAlignment.Center }, r + 1, 0);
            for (int c = 0; c < 3; c++)
            {
                int bit = 8 - (r * 3 + c);
                var cb = new CheckBox { IsChecked = (mode & (1 << bit)) != 0 };
                cb.Click += (_, _) => UpdateOctal();
                _bits[r * 3 + c] = cb;
                Add(grid, cb, r + 1, c + 1);
            }
        }
        root.Children.Add(grid);
        var octRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        octRow.Children.Add(new TextBlock { Text = "Octal", Style = (Style)FindResource("Text.Label") });
        octRow.Children.Add(_octal);
        root.Children.Add(octRow);
        _octal.TextChanged += (_, _) => FromOctal();
        UpdateOctal();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = "Apply", IsDefault = true, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => DialogResult = true;
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        root.Children.Add(bar);
        Content = root;
    }

    private static void Add(Grid g, UIElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        g.Children.Add(e);
    }

    private int Mode
    {
        get
        {
            int m = 0;
            for (int i = 0; i < 9; i++) if (_bits[i].IsChecked == true) m |= 1 << (8 - i);
            return m;
        }
    }

    private void UpdateOctal()
    {
        if (_sync) return;
        _sync = true;
        _octal.Text = Convert.ToString(Mode, 8).PadLeft(3, '0');
        _sync = false;
    }

    private void FromOctal()
    {
        if (_sync) return;
        if (!int.TryParse(_octal.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return;
        try
        {
            int m = Convert.ToInt32(_octal.Text, 8) & 0x1FF;
            _sync = true;
            for (int i = 0; i < 9; i++) _bits[i].IsChecked = (m & (1 << (8 - i))) != 0;
            _sync = false;
        }
        catch (FormatException) { }
    }

    public static int? Ask(string name, int mode) => OnUi(() =>
    {
        var d = new ChmodDialog(name, mode);
        return d.ShowDialog() == true ? d.Mode : (int?)null;
    });
}
