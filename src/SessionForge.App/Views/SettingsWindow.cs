using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Core.Storage;
using SessionForge.Terminal;

namespace SessionForge.App.Views;

/// <summary>Application settings.</summary>
public sealed class SettingsWindow : DialogBase
{
    private readonly AppSettings _s;
    private readonly List<Action> _apply = [];

    private SettingsWindow()
    {
        _s = JsonFile.Clone(AppServices.Settings);
        Title = "Settings";
        Width = 700;
        Height = 620;
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        var tabs = new TabControl { Style = (Style)FindResource("Tabs.Pill"), Margin = new Thickness(20, 16, 20, 0) };
        tabs.Items.Add(Page("Appearance", AppearancePage()));
        tabs.Items.Add(Page("Terminal", TerminalPage()));
        tabs.Items.Add(Page("Logging", LoggingPage()));
        tabs.Items.Add(Page("SSH & SFTP", SshPage()));
        tabs.Items.Add(Page("Security", SecurityPage()));
        tabs.Items.Add(Page("Startup", StartupPage()));
        var root = new DockPanel();
        var footer = new Border { Padding = new Thickness(20, 12, 20, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 96, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Save();
        bar.Children.Add(ok);
        bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) });
        footer.Child = bar;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
    }

    public static bool Open() => new SettingsWindow().ShowDialog() == true;

    private static TabItem Page(string header, UIElement content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 10) } };

    private StackPanel Section(StackPanel parent, string title)
    {
        parent.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = (Style)FindResource("Text.Section") });
        return parent;
    }

    private void Check(StackPanel p, string label, Func<bool> get, Action<bool> set, string? hint = null)
    {
        var cb = new CheckBox { Content = label, IsChecked = get() };
        _apply.Add(() => set(cb.IsChecked == true));
        p.Children.Add(cb);
        if (hint != null) p.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(26, 0, 0, 6) });
    }

    private T Field<T>(StackPanel p, string label, T control) where T : FrameworkElement
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var l = new TextBlock { Text = label, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        row.Children.Add(l);
        row.Children.Add(control);
        p.Children.Add(row);
        return control;
    }

    private void Number(StackPanel p, string label, Func<int> get, Action<int> set, int min, int max)
    {
        var tb = Field(p, label, new TextBox { Text = get().ToString(CultureInfo.CurrentCulture), Width = 120, HorizontalAlignment = HorizontalAlignment.Left });
        _apply.Add(() => { if (int.TryParse(tb.Text, out var v)) set(Math.Clamp(v, min, max)); });
    }

    private void Combo<T>(StackPanel p, string label, (T Value, string Label)[] options, Func<T> get, Action<T> set) where T : notnull
    {
        var cb = Field(p, label, new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left });
        var opts = options.Select(o => new Option<T>(o.Value, o.Label)).ToArray();
        cb.ItemsSource = opts;
        cb.SelectedItem = opts.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, get())) ?? opts[0];
        _apply.Add(() => { if (cb.SelectedItem is Option<T> o) set(o.Value); });
    }

    private UIElement AppearancePage()
    {
        var p = new StackPanel();
        Section(p, "Application");
        Combo(p, "Theme", [(AppTheme.Dark, "Dark"), (AppTheme.Light, "Light"), (AppTheme.System, "Follow Windows")], () => _s.Theme, v => _s.Theme = v);
        Section(p, "Terminal");
        Combo(p, "Colour scheme", TerminalTheme.BuiltIn.Select(t => (t.Name, t.Name)).ToArray(), () => _s.TerminalTheme, v => _s.TerminalTheme = v);
        var font = Field(p, "Font", new ComboBox { IsEditable = true, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = SessionEditorWindow.MonospaceFonts() });
        font.Text = _s.TerminalFontFamily.Split(',')[0].Trim();
        _apply.Add(() => { if (!string.IsNullOrWhiteSpace(font.Text)) _s.TerminalFontFamily = font.Text.Trim() + ", Cascadia Mono, Consolas"; });
        var size = Field(p, "Font size", new TextBox { Text = _s.TerminalFontSize.ToString(CultureInfo.CurrentCulture), Width = 120, HorizontalAlignment = HorizontalAlignment.Left });
        _apply.Add(() => { if (double.TryParse(size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) _s.TerminalFontSize = Math.Clamp(v, 6, 72); });
        Check(p, "Blinking cursor", () => _s.CursorBlink, v => _s.CursorBlink = v);
        Check(p, "Show status bar", () => _s.ShowStatusBar, v => _s.ShowStatusBar = v);
        return p;
    }

    private UIElement TerminalPage()
    {
        var p = new StackPanel();
        Section(p, "Behaviour");
        Number(p, "Scrollback lines", () => _s.ScrollbackLines, v => _s.ScrollbackLines = v, 100, 1_000_000);
        Check(p, "Copy selected text automatically", () => _s.CopyOnSelect, v => _s.CopyOnSelect = v);
        Check(p, "Right-click pastes (Shift+right-click opens the menu)", () => _s.RightClickPastes, v => _s.RightClickPastes = v);
        Combo(p, "Paste confirmation", [(PasteWarning.MultiLine, "Multi-line pastes and control characters"), (PasteWarning.Always, "Always"), (PasteWarning.Never, "Only control characters")], () => _s.PasteWarning, v => _s.PasteWarning = v);
        Check(p, "Highlight keywords by default (errors, warnings, OK, IP addresses)", () => _s.KeywordHighlighting, v => _s.KeywordHighlighting = v);
        var delims = Field(p, "Word separators (double-click)", new TextBox { Text = _s.WordDelimiters, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        _apply.Add(() => _s.WordDelimiters = delims.Text);
        Check(p, "Allow remote programs to set the clipboard (OSC 52)", () => _s.AllowOsc52Clipboard, v => _s.AllowOsc52Clipboard = v,
            "Off by default: a compromised server could place malicious commands on your clipboard. Clipboard reads are never allowed.");
        Section(p, "Tabs");
        Check(p, "Confirm before closing connected sessions", () => _s.ConfirmCloseConnectedTab, v => _s.ConfirmCloseConnectedTab = v);
        Check(p, "Offer to reconnect when a session ends", () => _s.ReconnectPrompt, v => _s.ReconnectPrompt = v);
        return p;
    }

    private UIElement LoggingPage()
    {
        var p = new StackPanel();
        Section(p, "Session logging");
        Combo(p, "Default for new sessions", [(LogMode.Off, "Off"), (LogMode.PlainText, "Plain text"), (LogMode.PlainTextWithTimestamps, "Plain text with timestamps"), (LogMode.Raw, "Raw (byte-exact)")], () => _s.DefaultLogMode, v => _s.DefaultLogMode = v);
        var dir = Field(p, "Log folder", new TextBox { Text = _s.LogDirectory ?? string.Empty, Tag = AppServices.Paths.LogDirectory });
        _apply.Add(() => _s.LogDirectory = string.IsNullOrWhiteSpace(dir.Text) ? null : dir.Text.Trim());
        var pattern = Field(p, "File name pattern", new TextBox { Text = _s.LogFileNamePattern });
        _apply.Add(() => { if (!string.IsNullOrWhiteSpace(pattern.Text)) _s.LogFileNamePattern = pattern.Text.Trim(); });
        p.Children.Add(new TextBlock { Text = "Placeholders: {session}, {date:yyyyMMdd_HHmmss}. Logs may contain sensitive output; protect the folder accordingly.", Style = (Style)FindResource("Text.Hint") });
        return p;
    }

    private UIElement SshPage()
    {
        var p = new StackPanel();
        Section(p, "SSH");
        Check(p, "Ask before trusting unknown host keys (recommended)", () => _s.StrictHostKeyChecking, v => _s.StrictHostKeyChecking = v,
            "When off, unknown keys are trusted automatically on first use. Changed keys always require confirmation.");
        Number(p, "Default keepalive (seconds)", () => _s.DefaultKeepAliveSeconds, v => _s.DefaultKeepAliveSeconds = v, 0, 3600);
        Check(p, "Use keys from the Windows OpenSSH agent / Pageant when available", () => _s.UseWindowsSshAgent, v => _s.UseWindowsSshAgent = v);
        Section(p, "SFTP");
        Check(p, "Open the SFTP browser automatically for SSH sessions", () => _s.AutoOpenSftp, v => _s.AutoOpenSftp = v);
        Check(p, "Preserve file modification times on transfer", () => _s.PreserveFileTimesOnTransfer, v => _s.PreserveFileTimesOnTransfer = v);
        Number(p, "Concurrent transfers", () => _s.MaxConcurrentTransfers, v => _s.MaxConcurrentTransfers = v, 1, 8);
        return p;
    }

    private UIElement SecurityPage()
    {
        var p = new StackPanel();
        Section(p, "Credential vault");
        Number(p, "Auto-lock after (minutes, 0 = never)", () => _s.VaultAutoLockMinutes, v => _s.VaultAutoLockMinutes = v, 0, 24 * 60);
        Check(p, "Ask for the master password before revealing or copying secrets", () => _s.RequireReauthToRevealPasswords, v => _s.RequireReauthToRevealPasswords = v);
        Number(p, "Clear copied secrets from the clipboard after (seconds, 0 = never)", () => _s.ClipboardClearSeconds, v => _s.ClipboardClearSeconds = v, 0, 3600);
        var vaultBtn = new Button { Content = "Open credential vault…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        vaultBtn.Click += (_, _) => VaultWindow.Open();
        p.Children.Add(vaultBtn);
        return p;
    }

    private UIElement StartupPage()
    {
        var p = new StackPanel();
        Section(p, "Startup");
        Check(p, "Reopen the sessions that were open when SessionForge was closed", () => _s.RestoreSessionsOnStartup, v => _s.RestoreSessionsOnStartup = v);
        p.Children.Add(new TextBlock { Text = $"Data folder: {AppServices.Paths.Root}\nCreate an empty file named 'portable' next to SessionForge.exe to keep all data beside the program instead.", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 12, 0, 0) });
        return p;
    }

    private void Save()
    {
        foreach (var a in _apply) a();
        var themeChanged = _s.Theme != AppServices.Settings.Theme;
        AppServices.SettingsStore.Replace(_s);
        if (themeChanged) ThemeManager.Apply(_s.Theme);
        DialogResult = true;
    }
}
