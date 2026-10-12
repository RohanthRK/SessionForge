using System.Windows;
using System.Windows.Controls;
using SessionForge.Protocols;

namespace SessionForge.App.Dialogs;

/// <summary>Generic credential/value prompt with optional "save in vault" checkbox.</summary>
public sealed class PromptDialog : DialogBase
{
    private readonly List<Control> _inputs = [];
    private readonly CheckBox? _save;

    public PromptDialog(string title, string message, IReadOnlyList<PromptField> fields, bool offerSave, bool saveDefault = false, string saveLabel = "Save in the credential vault")
    {
        Title = title;
        Width = 440;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(message))
            root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Fg.Secondary"), Margin = new Thickness(0, 0, 0, 14) });

        foreach (var f in fields)
        {
            root.Children.Add(new TextBlock { Text = f.Label.TrimEnd(':', ' '), Margin = new Thickness(0, 6, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("Fg.Secondary") });
            Control input = f.Secret
                ? new PasswordBox { Password = f.DefaultValue ?? string.Empty }
                : new TextBox { Text = f.DefaultValue ?? string.Empty };
            _inputs.Add(input);
            root.Children.Add(input);
        }

        if (offerSave)
        {
            _save = new CheckBox { Content = saveLabel, IsChecked = saveDefault, Margin = new Thickness(0, 14, 0, 0) };
            root.Children.Add(_save);
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 88, IsDefault = true, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);
        Content = root;

        Loaded += (_, _) =>
        {
            var first = _inputs.FirstOrDefault();
            first?.Focus();
            if (first is TextBox tb) tb.SelectAll();
        };
    }

    public string[] Values => _inputs.Select(c => c is PasswordBox p ? p.Password : ((TextBox)c).Text).ToArray();
    public bool SaveChecked => _save?.IsChecked == true;

    public static PromptResult? Ask(string title, string message, IReadOnlyList<PromptField> fields, bool offerSave, bool saveDefault = false)
    {
        return OnUi(() =>
        {
            var d = new PromptDialog(title, message, fields, offerSave, saveDefault);
            return d.ShowDialog() == true ? new PromptResult(d.Values, d.SaveChecked) : null;
        });
    }

    public static string? AskText(string title, string message, string label, string? initial = null)
    {
        var r = Ask(title, message, [new PromptField(label, false, initial)], false);
        return r?.Values[0];
    }
}
