using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Core.Storage;
using SessionForge.Protocols.Serial;
using SessionForge.Terminal;

namespace SessionForge.App.Views;

public sealed record KindOption(SessionKind Kind, string Glyph, string Label);
public sealed record Option<T>(T Value, string Label) { public override string ToString() => Label; }
public sealed record FolderOption(Guid? Id, string Path) { public override string ToString() => Path; }
public sealed record IdName(Guid? Id, string Name) { public override string ToString() => Name; }

public sealed class JumpVm(JumpHost jump)
{
    public JumpHost Jump { get; } = jump;
    public string Display => Jump.SessionId is { } id
        ? $"↪ saved session: {AppServices.Sessions.Get(id)?.Name ?? "(missing)"}"
        : $"{(string.IsNullOrEmpty(Jump.Credential.Username) ? "" : Jump.Credential.Username + "@")}{Jump.Host}:{Jump.Port}  ({Jump.Auth})";
}

public partial class SessionEditorWindow : Window
{
    private static readonly KindOption[] Kinds =
    [
        new(SessionKind.Ssh, "", "SSH"),
        new(SessionKind.Sftp, "", "SFTP"),
        new(SessionKind.Rdp, "", "RDP"),
        new(SessionKind.Vnc, "", "VNC"),
        new(SessionKind.Telnet, "", "Telnet"),
        new(SessionKind.Serial, "", "Serial"),
        new(SessionKind.LocalShell, "", "Local shell"),
        new(SessionKind.Raw, "", "Raw TCP"),
    ];

    private static readonly string[] Colors = ["", "#EF4444", "#F97316", "#EAB308", "#22C55E", "#14B8A6", "#3B82F6", "#8B5CF6", "#EC4899", "#64748B"];

    private readonly Session _session;
    private readonly bool _isNew;
    private readonly ObservableCollection<JumpVm> _jumps = [];
    private readonly ObservableCollection<PortForward> _forwards = [];
    private string? _color;

    public SessionEditorWindow(Session session, bool isNew)
    {
        InitializeComponent();
        ThemeManager.ApplyChrome(this);
        Owner = DialogBase.FindOwner();
        _isNew = isNew;
        _session = JsonFile.Clone(session);
        DataContext = _session;
        Title = isNew ? "New session" : $"Edit session — {session.Name}";

        KindList.ItemsSource = Kinds;
        KindList.SelectedItem = Kinds.First(k => k.Kind == _session.Kind);

        AuthCombo.ItemsSource = new[]
        {
            new Option<AuthMethod>(AuthMethod.Password, "Password"),
            new Option<AuthMethod>(AuthMethod.PublicKey, "Public key (key file or vault key)"),
            new Option<AuthMethod>(AuthMethod.KeyboardInteractive, "Keyboard-interactive (OTP / 2FA prompts)"),
            new Option<AuthMethod>(AuthMethod.Agent, "SSH agent (Windows OpenSSH agent / Pageant)"),
        };
        AuthCombo.SelectedItem = ((Option<AuthMethod>[])AuthCombo.ItemsSource).First(o => o.Value == _session.Ssh.Auth);

        LoadVaultLists();

        var folders = new List<FolderOption> { new(null, "(top level)") };
        folders.AddRange(AppServices.Sessions.Folders.Select(f => new FolderOption(f.Id, AppServices.Sessions.FolderPath(f.Id))).OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase));
        FolderCombo.ItemsSource = folders;
        FolderCombo.SelectedValue = _session.FolderId;

        TagsBox.Text = string.Join(", ", _session.Tags);
        _color = _session.TabColor;
        BuildColorSwatches();

        foreach (var j in _session.Ssh.JumpHosts) _jumps.Add(new JumpVm(j));
        JumpList.ItemsSource = _jumps;
        foreach (var f in _session.Ssh.Forwards) _forwards.Add(f);
        ForwardList.ItemsSource = _forwards;

        ProxyCombo.ItemsSource = new[] { new Option<ProxyKind>(ProxyKind.None, "None"), new Option<ProxyKind>(ProxyKind.Socks5, "SOCKS5"), new Option<ProxyKind>(ProxyKind.Socks4, "SOCKS4"), new Option<ProxyKind>(ProxyKind.Http, "HTTP CONNECT") };
        ProxyCombo.SelectedItem = ((Option<ProxyKind>[])ProxyCombo.ItemsSource).First(o => o.Value == _session.Ssh.Proxy.Kind);
        TermTypeCombo.ItemsSource = new[] { "xterm-256color", "xterm", "vt220", "vt100", "linux", "screen-256color", "tmux-256color" };
        KexBox.Text = string.Join(", ", _session.Ssh.KexAlgorithms);
        CipherBox.Text = string.Join(", ", _session.Ssh.Ciphers);
        HostKeyAlgBox.Text = string.Join(", ", _session.Ssh.HostKeyAlgorithms);

        ThemeCombo.ItemsSource = new[] { "(default)" }.Concat(TerminalTheme.BuiltIn.Select(t => t.Name)).ToList();
        ThemeCombo.SelectedItem = _session.Terminal.ThemeName ?? "(default)";
        FontCombo.ItemsSource = MonospaceFonts();
        FontCombo.Text = _session.Terminal.FontFamily ?? string.Empty;
        FontSizeBox.Text = _session.Terminal.FontSize?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        EncodingCombo.ItemsSource = new[] { "utf-8", "iso-8859-1", "iso-8859-2", "iso-8859-5", "iso-8859-7", "iso-8859-9", "iso-8859-15", "windows-1250", "windows-1251", "windows-1252", "koi8-r", "koi8-u", "gbk", "big5", "shift_jis", "euc-kr", "ibm437", "ibm850" };
        LogCombo.ItemsSource = new[]
        {
            new Option<LogMode>(LogMode.Off, "Use default (Settings)"),
            new Option<LogMode>(LogMode.PlainText, "Plain text"),
            new Option<LogMode>(LogMode.PlainTextWithTimestamps, "Plain text with timestamps"),
            new Option<LogMode>(LogMode.Raw, "Raw (byte-exact, replayable)"),
        };
        LogCombo.SelectedItem = ((Option<LogMode>[])LogCombo.ItemsSource).First(o => o.Value == _session.Terminal.Logging);
        var macros = new List<IdName> { new(null, "(none)") };
        macros.AddRange(AppServices.Macros.Value.Macros.Select(m => new IdName(m.Id, m.Name)));
        MacroCombo.ItemsSource = macros;
        MacroCombo.SelectedValue = _session.Terminal.StartupMacroId;

        ResolutionCombo.ItemsSource = new[] { "Fit to tab", "1024x768", "1280x720", "1280x1024", "1366x768", "1440x900", "1600x900", "1680x1050", "1920x1080", "1920x1200", "2560x1440", "3840x2160" };
        ResolutionCombo.SelectedItem = _session.Rdp.Width > 0 ? $"{_session.Rdp.Width}x{_session.Rdp.Height}" : "Fit to tab";
        DepthCombo.ItemsSource = new[] { new Option<int>(32, "32-bit"), new Option<int>(24, "24-bit"), new Option<int>(16, "16-bit"), new Option<int>(15, "15-bit") };
        DepthCombo.SelectedItem = ((Option<int>[])DepthCombo.ItemsSource).FirstOrDefault(o => o.Value == _session.Rdp.ColorDepth) ?? ((Option<int>[])DepthCombo.ItemsSource)[0];
        AudioCombo.ItemsSource = new[] { new Option<int>(0, "Play on this computer"), new Option<int>(1, "Play on the remote computer"), new Option<int>(2, "Do not play") };
        AudioCombo.SelectedItem = ((Option<int>[])AudioCombo.ItemsSource)[Math.Clamp(_session.Rdp.AudioMode, 0, 2)];
        RdpAuthCombo.ItemsSource = new[] { new Option<int>(0, "Connect without warning"), new Option<int>(1, "Do not connect if verification fails"), new Option<int>(2, "Warn me if verification fails") };
        RdpAuthCombo.SelectedItem = ((Option<int>[])RdpAuthCombo.ItemsSource)[Math.Clamp(_session.Rdp.AuthenticationLevel, 0, 2)];

        SerialPortCombo.ItemsSource = SerialTerminalTransport.AvailablePorts();
        BaudCombo.ItemsSource = new[] { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
        DataBitsCombo.ItemsSource = new[] { 8, 7, 6, 5 };
        DataBitsCombo.SelectedItem = _session.Serial.DataBits;
        ParityCombo.ItemsSource = Enum.GetValues<SerialParity>();
        ParityCombo.SelectedItem = _session.Serial.Parity;
        StopBitsCombo.ItemsSource = new[] { new Option<SerialStopBits>(SerialStopBits.One, "1"), new Option<SerialStopBits>(SerialStopBits.OnePointFive, "1.5"), new Option<SerialStopBits>(SerialStopBits.Two, "2") };
        StopBitsCombo.SelectedItem = ((Option<SerialStopBits>[])StopBitsCombo.ItemsSource).First(o => o.Value == _session.Serial.StopBits);
        FlowCombo.ItemsSource = new[] { new Option<SerialFlowControl>(SerialFlowControl.None, "None"), new Option<SerialFlowControl>(SerialFlowControl.XonXoff, "XON/XOFF"), new Option<SerialFlowControl>(SerialFlowControl.RtsCts, "RTS/CTS (hardware)"), new Option<SerialFlowControl>(SerialFlowControl.DsrDtr, "DSR/DTR") };
        FlowCombo.SelectedItem = ((Option<SerialFlowControl>[])FlowCombo.ItemsSource).First(o => o.Value == _session.Serial.FlowControl);

        ShellCombo.ItemsSource = LocalShells();

        UpdateKindVisibility();
        Loaded += (_, _) => { (string.IsNullOrEmpty(_session.Host) && _session.Kind is not (SessionKind.Serial or SessionKind.LocalShell) ? HostBox : NameBox).Focus(); };
    }

    public Session? Result { get; private set; }
    public bool ConnectRequested { get; private set; }

    public static IEnumerable<string> LocalShells()
    {
        var list = new List<string> { "powershell.exe -NoLogo", "cmd.exe" };
        var pwsh = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe");
        if (System.IO.File.Exists(pwsh)) list.Insert(0, "pwsh.exe -NoLogo");
        if (System.IO.File.Exists(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\wsl.exe"))) list.Add("wsl.exe");
        var gitBash = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Git\bin\bash.exe");
        if (System.IO.File.Exists(gitBash)) list.Add($"\"{gitBash}\" --login -i");
        return list;
    }

    public static List<string> MonospaceFonts()
    {
        var known = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New", "JetBrains Mono", "Fira Code", "Fira Mono", "Source Code Pro", "Hack", "DejaVu Sans Mono", "Ubuntu Mono", "Iosevka", "Inconsolata", "Roboto Mono", "MesloLGS NF", "CaskaydiaCove Nerd Font" };
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return known.Where(installed.Contains).ToList();
    }

    private void LoadVaultLists()
    {
        var creds = new List<IdName> { new(null, "(none — use username/password above)") };
        var keys = new List<IdName> { new(null, "(no vault key)") };
        if (AppServices.Vault.IsUnlocked)
        {
            creds.AddRange(AppServices.Vault.Credentials.Select(c => new IdName(c.Id, $"{c.Name} ({c.Username})")));
            keys.AddRange(AppServices.Vault.Keys.Select(k => new IdName(k.Id, k.Name)));
        }
        else if (AppServices.Vault.Exists)
        {
            creds.Add(new IdName(Guid.Empty, "🔒 Unlock vault to list credentials…"));
        }
        CredentialCombo.ItemsSource = creds;
        CredentialCombo.SelectedValue = _session.Credential.CredentialId;
        if (CredentialCombo.SelectedIndex < 0) CredentialCombo.SelectedIndex = 0;
        CredentialCombo.SelectionChanged -= CredentialCombo_SelectionChanged;
        CredentialCombo.SelectionChanged += CredentialCombo_SelectionChanged;
        VaultKeyCombo.ItemsSource = keys;
        VaultKeyCombo.SelectedValue = _session.Ssh.VaultKeyId;
        if (VaultKeyCombo.SelectedIndex < 0) VaultKeyCombo.SelectedIndex = 0;
    }

    private void CredentialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CredentialCombo.SelectedValue is Guid g && g == Guid.Empty)
        {
            if (UnlockVaultDialog.EnsureUnlocked()) LoadVaultLists();
            else CredentialCombo.SelectedIndex = 0;
        }
    }

    private void BuildColorSwatches()
    {
        ColorPanel.Children.Clear();
        foreach (var c in Colors)
        {
            var color = c;
            var swatch = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(_color == (c.Length == 0 ? null : c) ? 3 : 1),
                ToolTip = c.Length == 0 ? "No colour" : c,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, _color == (c.Length == 0 ? null : c) ? "Fg.Primary" : "Border.Strong");
            if (c.Length == 0) swatch.SetResourceReference(Border.BackgroundProperty, "Bg.Control");
            else swatch.Background = (Brush)new BrushConverter().ConvertFromString(c)!;
            swatch.MouseLeftButtonUp += (_, _) => { _color = color.Length == 0 ? null : color; BuildColorSwatches(); };
            ColorPanel.Children.Add(swatch);
        }
    }

    private SessionKind SelectedKind => (KindList.SelectedItem as KindOption)?.Kind ?? SessionKind.Ssh;

    private void KindList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = SelectedKind;
        if (kind != _session.Kind)
        {
            // Adjust the default port when switching type (only if the port is still a default).
            if (_session.Port == 0 || _session.Port == Session.DefaultPort(_session.Kind)) _session.Port = Session.DefaultPort(kind);
            _session.Kind = kind;
            PortBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
        UpdateKindVisibility();
    }

    private void UpdateKindVisibility()
    {
        var k = SelectedKind;
        bool net = k is SessionKind.Ssh or SessionKind.Sftp or SessionKind.Telnet or SessionKind.Rdp or SessionKind.Vnc or SessionKind.Raw;
        bool ssh = k is SessionKind.Ssh or SessionKind.Sftp;
        bool term = k is SessionKind.Ssh or SessionKind.Telnet or SessionKind.Serial or SessionKind.LocalShell or SessionKind.Raw;
        bool hasUser = k is SessionKind.Ssh or SessionKind.Sftp or SessionKind.Rdp;
        void Show(UIElement e, bool v) => e.Visibility = v ? Visibility.Visible : Visibility.Collapsed;

        Show(HostLabel, net); Show(HostBox, net); Show(PortLabel, net); Show(PortBox, net);
        Show(UserLabel, hasUser); Show(UserBox, hasUser);
        Show(CredLabel, hasUser); Show(CredentialCombo, hasUser);
        Show(AuthLabel, ssh); Show(AuthCombo, ssh);
        bool key = ssh && (AuthCombo.SelectedItem as Option<AuthMethod>)?.Value == AuthMethod.PublicKey;
        Show(KeyLabel, key); Show(KeyPanel, key);
        Show(SavePasswordCheck, hasUser || k == SessionKind.Vnc);
        Show(PasswordRow, hasUser || k == SessionKind.Vnc);
        Show(SerialPortLabel, k == SessionKind.Serial); Show(SerialPortCombo, k == SessionKind.Serial);
        Show(BaudLabel, k == SessionKind.Serial); Show(BaudCombo, k == SessionKind.Serial);
        Show(ShellLabel, k == SessionKind.LocalShell); Show(ShellCombo, k == SessionKind.LocalShell);
        Show(ShellDirLabel, k == SessionKind.LocalShell); Show(ShellDirBox, k == SessionKind.LocalShell);

        Show(SshTab, ssh || k is SessionKind.Rdp or SessionKind.Vnc);
        Show(ForwardTab, k == SessionKind.Ssh);
        Show(TerminalTab, term);
        Show(RdpTab, k == SessionKind.Rdp);
        Show(VncTab, k == SessionKind.Vnc);
        Show(SerialTab, k == SessionKind.Serial);
        if (Tabs.SelectedItem is TabItem { Visibility: not Visibility.Visible }) Tabs.SelectedIndex = 0;
    }

    private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateKindVisibility();

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var sshDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dlg = new OpenFileDialog
        {
            Title = "Select private key",
            Filter = "Private keys|id_*;*.pem;*.ppk;*.key|All files|*.*",
            InitialDirectory = System.IO.Directory.Exists(sshDir) ? sshDir : null,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _session.Ssh.PrivateKeyPath = dlg.FileName;
            var tb = KeyPanel.Children.OfType<TextBox>().First();
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
    }

    #region Jump hosts

    private void AddJump_Click(object sender, RoutedEventArgs e)
    {
        var j = JumpHostDialog.Edit(new JumpHost());
        if (j != null) _jumps.Add(new JumpVm(j));
    }

    private void AddJumpSession_Click(object sender, RoutedEventArgs e)
    {
        var picked = SessionPickerDialog.Pick("Select an SSH session to use as jump host", s => s.Kind == SessionKind.Ssh && s.Id != _session.Id);
        if (picked != null) _jumps.Add(new JumpVm(new JumpHost { SessionId = picked.Id }));
    }

    private void JumpUp_Click(object sender, RoutedEventArgs e)
    {
        int i = JumpList.SelectedIndex;
        if (i > 0) { _jumps.Move(i, i - 1); JumpList.SelectedIndex = i - 1; }
    }

    private void JumpDown_Click(object sender, RoutedEventArgs e)
    {
        int i = JumpList.SelectedIndex;
        if (i >= 0 && i < _jumps.Count - 1) { _jumps.Move(i, i + 1); JumpList.SelectedIndex = i + 1; }
    }

    private void JumpRemove_Click(object sender, RoutedEventArgs e)
    {
        if (JumpList.SelectedItem is JumpVm vm) _jumps.Remove(vm);
    }

    #endregion

    #region Forwards

    private void AddForward_Click(object sender, RoutedEventArgs e)
    {
        var f = ForwardDialog.Edit(new PortForward { Kind = ForwardKind.Local, BindPort = 8080, TargetHost = "localhost", TargetPort = 80 });
        if (f != null) _forwards.Add(f);
    }

    private void EditForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForwardList.SelectedItem is not PortForward f) return;
        var edited = ForwardDialog.Edit(JsonFile.Clone(f));
        if (edited == null) return;
        int i = _forwards.IndexOf(f);
        _forwards[i] = edited;
    }

    private void RemoveForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForwardList.SelectedItem is PortForward f) _forwards.Remove(f);
    }

    #endregion

    private static List<string> SplitList(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private bool Collect()
    {
        ValidationText.Text = string.Empty;
        var s = _session;
        s.Kind = SelectedKind;
        s.Name = s.Name.Trim();
        s.Host = s.Host.Trim();
        if (string.IsNullOrWhiteSpace(s.Name))
            s.Name = s.Kind switch { SessionKind.Serial => s.Serial.PortName, SessionKind.LocalShell => s.Shell.CommandLine.Split(' ')[0], _ => s.Host };
        if (s.Kind is SessionKind.Ssh or SessionKind.Sftp or SessionKind.Telnet or SessionKind.Rdp or SessionKind.Vnc or SessionKind.Raw)
        {
            if (string.IsNullOrWhiteSpace(s.Host)) { ValidationText.Text = "Enter a host name or IP address."; Tabs.SelectedIndex = 0; return false; }
            if (s.Host.Contains(' ')) { ValidationText.Text = "The host name cannot contain spaces."; return false; }
            if (s.Port is <= 0 or > 65535) { ValidationText.Text = "Port must be between 1 and 65535."; return false; }
        }
        if (s.Kind == SessionKind.Serial && string.IsNullOrWhiteSpace(s.Serial.PortName)) { ValidationText.Text = "Choose a serial port."; return false; }
        if (s.Kind == SessionKind.Serial && s.Serial.BaudRate <= 0) { ValidationText.Text = "Enter a valid speed."; return false; }
        if (s.Kind == SessionKind.LocalShell && string.IsNullOrWhiteSpace(s.Shell.CommandLine)) { ValidationText.Text = "Enter a command to run."; return false; }

        s.FolderId = FolderCombo.SelectedValue as Guid?;
        s.Tags = SplitList(TagsBox.Text.Replace(' ', ','));
        s.TabColor = _color;
        s.Credential.CredentialId = CredentialCombo.SelectedValue is Guid cg && cg != Guid.Empty ? cg : null;
        s.Ssh.Auth = (AuthCombo.SelectedItem as Option<AuthMethod>)?.Value ?? AuthMethod.Password;
        s.Ssh.VaultKeyId = VaultKeyCombo.SelectedValue as Guid?;
        if (s.Ssh.Auth == AuthMethod.PublicKey && string.IsNullOrWhiteSpace(s.Ssh.PrivateKeyPath) && s.Ssh.VaultKeyId == null && s.Kind is SessionKind.Ssh or SessionKind.Sftp)
        {
            ValidationText.Text = "Select a private key file or a key from the vault.";
            return false;
        }
        s.Ssh.JumpHosts = _jumps.Select(j => j.Jump).ToList();
        s.Ssh.Forwards = _forwards.ToList();
        s.Ssh.Proxy.Kind = (ProxyCombo.SelectedItem as Option<ProxyKind>)?.Value ?? ProxyKind.None;
        s.Ssh.KexAlgorithms = SplitList(KexBox.Text);
        s.Ssh.Ciphers = SplitList(CipherBox.Text);
        s.Ssh.HostKeyAlgorithms = SplitList(HostKeyAlgBox.Text);

        s.Terminal.ThemeName = ThemeCombo.SelectedItem as string is { } t && t != "(default)" ? t : null;
        s.Terminal.FontFamily = string.IsNullOrWhiteSpace(FontCombo.Text) ? null : FontCombo.Text.Trim();
        s.Terminal.FontSize = double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var fs) && fs is >= 6 and <= 72 ? fs : null;
        try { System.Text.Encoding.GetEncoding(s.Terminal.Encoding); }
        catch (ArgumentException) { ValidationText.Text = $"Unknown character set '{s.Terminal.Encoding}'."; return false; }
        s.Terminal.Logging = (LogCombo.SelectedItem as Option<LogMode>)?.Value ?? LogMode.Off;
        s.Terminal.StartupMacroId = MacroCombo.SelectedValue as Guid?;
        s.Terminal.ScrollbackLines = Math.Clamp(s.Terminal.ScrollbackLines, 100, 1_000_000);

        if (ResolutionCombo.SelectedItem is string res && res.Contains('x'))
        {
            var p = res.Split('x');
            s.Rdp.Width = int.Parse(p[0], CultureInfo.InvariantCulture);
            s.Rdp.Height = int.Parse(p[1], CultureInfo.InvariantCulture);
        }
        else { s.Rdp.Width = 0; s.Rdp.Height = 0; }
        s.Rdp.ColorDepth = (DepthCombo.SelectedItem as Option<int>)?.Value ?? 32;
        s.Rdp.AudioMode = (AudioCombo.SelectedItem as Option<int>)?.Value ?? 0;
        s.Rdp.AuthenticationLevel = (RdpAuthCombo.SelectedItem as Option<int>)?.Value ?? 2;

        if (DataBitsCombo.SelectedItem is int db) s.Serial.DataBits = db;
        if (ParityCombo.SelectedItem is SerialParity par) s.Serial.Parity = par;
        s.Serial.StopBits = (StopBitsCombo.SelectedItem as Option<SerialStopBits>)?.Value ?? SerialStopBits.One;
        s.Serial.FlowControl = (FlowCombo.SelectedItem as Option<SerialFlowControl>)?.Value ?? SerialFlowControl.None;

        // Password typed here is stored in the vault, never in the session file.
        if (PasswordBox.Password.Length > 0)
        {
            if (!UnlockVaultDialog.EnsureAvailable("Saving the password requires the encrypted credential vault."))
            {
                ValidationText.Text = "The password was not saved (vault unavailable).";
                return false;
            }
            AppServices.Vault.SetSecret($"session:{s.Id}:password", PasswordBox.Password);
            s.Credential.SavePassword = true;
        }
        else if (!s.Credential.SavePassword && AppServices.Vault.IsUnlocked)
        {
            // Unticked "save password": forget anything stored for this session.
            AppServices.Vault.RemoveSecretsWithPrefix($"session:{s.Id}:");
        }
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        Result = _session;
        DialogResult = true;
    }

    private void SaveConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        Result = _session;
        ConnectRequested = true;
        DialogResult = true;
    }

    /// <summary>Opens the editor; on success saves to the store and returns the session (and whether to connect).</summary>
    public static (Session? Session, bool Connect) Run(Session session, bool isNew)
    {
        var w = new SessionEditorWindow(session, isNew);
        if (w.ShowDialog() != true || w.Result == null) return (null, false);
        AppServices.Sessions.AddOrUpdate(w.Result);
        return (w.Result, w.ConnectRequested);
    }
}
