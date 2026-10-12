using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SessionForge.App.Dialogs;

public enum MessageKind { Info, Warning, Error, Question }

/// <summary>Themed replacement for MessageBox with custom button labels.</summary>
public sealed class MessageDialog : DialogBase
{
    private int _result = -1;

    private MessageDialog(string title, string message, string? detail, MessageKind kind, string[] buttons, int defaultIndex, int cancelIndex)
    {
        Title = title;
        Width = 480;
        var root = new Grid { Margin = new Thickness(24, 22, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (glyph, brushKey) = kind switch
        {
            MessageKind.Warning => ("", "Warning"),
            MessageKind.Error => ("", "Danger"),
            MessageKind.Question => ("", "Accent"),
            _ => ("", "Accent"),
        };
        var icon = new TextBlock { Text = glyph, FontSize = 26, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Style = (Style)FindResource("Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        root.Children.Add(icon);

        var texts = new StackPanel();
        Grid.SetColumn(texts, 1);
        texts.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var d = new TextBox
            {
                Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            texts.Children.Add(d);
        }
        root.Children.Add(texts);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), IsDefault = i == defaultIndex, IsCancel = i == cancelIndex };
            if (i == defaultIndex) b.Style = (Style)FindResource(kind == MessageKind.Warning && buttons.Length > 1 && buttons[i].Contains("Delete", StringComparison.OrdinalIgnoreCase) ? "Button.Danger" : "Button.Primary");
            b.Click += (_, _) => { _result = idx; DialogResult = true; };
            bar.Children.Add(b);
        }
        root.Children.Add(bar);
        Content = root;
    }

    /// <summary>Shows the dialog and returns the index of the clicked button, or -1 if closed.</summary>
    public static int Show(string title, string message, MessageKind kind = MessageKind.Info, string[]? buttons = null, int defaultIndex = 0, int cancelIndex = -1, string? detail = null)
    {
        return OnUi(() =>
        {
            var btns = buttons ?? ["OK"];
            var d = new MessageDialog(title, message, detail, kind, btns, defaultIndex, cancelIndex < 0 ? btns.Length - 1 : cancelIndex);
            d.ShowDialog();
            return d._result;
        });
    }

    public static void Info(string title, string message) => Show(title, message, MessageKind.Info);
    public static void Error(string title, string message, string? detail = null) => Show(title, message, MessageKind.Error, detail: detail);

    public static bool Confirm(string title, string message, string yes = "Yes", string no = "Cancel", MessageKind kind = MessageKind.Question) =>
        Show(title, message, kind, [yes, no], 0, 1) == 0;
}
