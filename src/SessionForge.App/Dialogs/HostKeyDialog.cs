using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SessionForge.Core.Security;
using SessionForge.Protocols;

namespace SessionForge.App.Dialogs;

/// <summary>Trust-on-first-use / changed host key confirmation.</summary>
public sealed class HostKeyDialog : DialogBase
{
    private HostKeyDecision _decision = HostKeyDecision.Reject;

    private HostKeyDialog(HostKeyInfo info)
    {
        bool changed = info.Status == HostKeyStatus.Changed;
        Title = changed ? "WARNING: host key changed" : "Unknown host key";
        Width = 600;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = changed ? "" : "", FontSize = 24, Margin = new Thickness(0, 0, 12, 0), Style = (Style)FindResource("Icon") };
        icon.SetResourceReference(TextBlock.ForegroundProperty, changed ? "Danger" : "Accent");
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = changed ? "The server's host key has CHANGED" : "First connection to this server", Style = (Style)FindResource("Text.Title"), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        var text = changed
            ? $"The host key presented by {info.Host}:{info.Port} does not match the key stored previously. This can mean someone is intercepting your connection (man-in-the-middle attack), or the server was reinstalled or its keys were rotated. Do not continue unless you have verified the new key with the server administrator."
            : $"The authenticity of {info.Host}:{info.Port} can't be established yet. Verify the fingerprint below with the server administrator (for example: ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub on the server) before trusting it.";
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), Foreground = (Brush)FindResource("Fg.Secondary") });

        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, string value, bool mono = true)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = (Brush)FindResource("Fg.Muted"), Margin = new Thickness(0, 3, 14, 3) };
            Grid.SetRow(l, r);
            var v = new TextBox
            {
                Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), MinHeight = 0,
                Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap,
            };
            if (mono) v.FontFamily = (FontFamily)FindResource("MonoFont");
            Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
        }
        Row("Host", $"{info.Host}:{info.Port}", false);
        Row("Key type", info.KeyType);
        Row("SHA-256", info.FingerprintSha256);
        Row("MD5", info.FingerprintMd5);
        if (changed && info.PreviousEntry != null)
        {
            Row("Previously", info.PreviousEntry.FingerprintSha256);
            Row("Trusted since", info.PreviousEntry.Added.LocalDateTime.ToString("g"), false);
        }
        card.Child = grid;
        root.Children.Add(card);

        // Paste-to-compare
        root.Children.Add(new TextBlock { Text = "Compare with an expected fingerprint (optional):", Margin = new Thickness(0, 14, 0, 4), Foreground = (Brush)FindResource("Fg.Secondary") });
        var compare = new TextBox { Tag = "Paste SHA256:… or MD5 fingerprint here" };
        var result = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.SemiBold };
        compare.TextChanged += (_, _) =>
        {
            var t = compare.Text.Trim();
            if (t.Length == 0) { result.Text = string.Empty; return; }
            bool match = string.Equals(t, info.FingerprintSha256, StringComparison.Ordinal)
                      || string.Equals(t, info.FingerprintSha256["SHA256:".Length..], StringComparison.Ordinal)
                      || string.Equals(t.Replace("MD5:", string.Empty, StringComparison.OrdinalIgnoreCase), info.FingerprintMd5, StringComparison.OrdinalIgnoreCase);
            result.Text = match ? "✔ Fingerprint matches" : "✘ Fingerprint does NOT match";
            result.SetResourceReference(TextBlock.ForegroundProperty, match ? "Success" : "Danger");
        };
        root.Children.Add(compare);
        root.Children.Add(result);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var save = new Button { Content = changed ? "Replace key and connect" : "Trust and connect", Style = (Style)FindResource(changed ? "Button.Danger" : "Button.Primary"), IsDefault = !changed };
        save.Click += (_, _) => { _decision = HostKeyDecision.AcceptAndSave; DialogResult = true; };
        var once = new Button { Content = "Connect once", Margin = new Thickness(8, 0, 0, 0) };
        once.Click += (_, _) => { _decision = HostKeyDecision.AcceptOnce; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = changed };
        bar.Children.Add(save);
        bar.Children.Add(once);
        bar.Children.Add(cancel);
        root.Children.Add(bar);
        Content = root;
    }

    public static HostKeyDecision Ask(HostKeyInfo info) => OnUi(() =>
    {
        var d = new HostKeyDialog(info);
        d.ShowDialog();
        return d._decision;
    });
}
