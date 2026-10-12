using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Security;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Views;

/// <summary>Generates SSH keys (Ed25519/RSA/ECDSA) and saves them to files and/or the vault.</summary>
public sealed class KeyManagerWindow : DialogBase
{
    private readonly ComboBox _type = new();
    private readonly ComboBox _bits = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _comment = new();
    private readonly PasswordBox _pass1 = new(), _pass2 = new();
    private readonly TextBox _public = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 76, FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Top };
    private readonly TextBlock _fingerprint = new() { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _saveFile = new() { Content = "Save to files…", IsEnabled = false };
    private readonly Button _saveVault = new() { Content = "Store in vault", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _copy = new() { Content = "Copy public key", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private GeneratedKey? _key;

    private KeyManagerWindow()
    {
        Title = "SSH key generator";
        Width = 600;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "Generate an SSH key pair", Style = (Style)FindResource("Text.Title"), Margin = new Thickness(0, 0, 0, 12) });
        var form = new FormBuilder();
        _type.ItemsSource = new[]
        {
            new Option<SshKeyType>(SshKeyType.Ed25519, "Ed25519 (recommended)"),
            new Option<SshKeyType>(SshKeyType.Rsa, "RSA"),
            new Option<SshKeyType>(SshKeyType.EcdsaP256, "ECDSA P-256"),
            new Option<SshKeyType>(SshKeyType.EcdsaP384, "ECDSA P-384"),
            new Option<SshKeyType>(SshKeyType.EcdsaP521, "ECDSA P-521"),
        };
        _type.SelectedIndex = 0;
        form.Add("Key type", _type);
        _bits.ItemsSource = new[] { 2048, 3072, 4096 };
        _bits.SelectedItem = 4096;
        form.Add("RSA bits", _bits);
        form.Add("Comment", _comment).Text = $"{Environment.UserName}@{Environment.MachineName}";
        form.Add("Passphrase", _pass1);
        form.Add("Confirm passphrase", _pass2);
        root.Children.Add(form.Grid);
        root.Children.Add(new TextBlock { Text = "With a passphrase the key is encrypted with bcrypt-pbkdf + AES-256-CTR (OpenSSH format). Keys stored in the vault are additionally protected by the vault.", Style = (Style)FindResource("Text.Hint") });
        var gen = new Button { Content = "Generate", Style = (Style)FindResource("Button.Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), MinWidth = 110 };
        gen.Click += async (_, _) => await GenerateAsync();
        root.Children.Add(gen);
        root.Children.Add(new TextBlock { Text = "PUBLIC KEY (add to ~/.ssh/authorized_keys)", Style = (Style)FindResource("Text.Section") });
        root.Children.Add(_public);
        root.Children.Add(_fingerprint);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(_saveFile);
        bar.Children.Add(_saveVault);
        bar.Children.Add(_copy);
        root.Children.Add(bar);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(close);
        Content = root;
        _type.SelectionChanged += (_, _) => _bits.IsEnabled = ((Option<SshKeyType>)_type.SelectedItem).Value == SshKeyType.Rsa;
        _bits.IsEnabled = false;
        _saveFile.Click += (_, _) => SaveFiles();
        _saveVault.Click += (_, _) => SaveVault();
        _copy.Click += (_, _) => { if (_key != null) Clipboard.SetText(_key.PublicKeyLine); };
    }

    public static void Open() => new KeyManagerWindow().ShowDialog();

    private async Task GenerateAsync()
    {
        if (_pass1.Password != _pass2.Password) { MessageDialog.Error("Key generator", "The passphrases do not match."); return; }
        var type = ((Option<SshKeyType>)_type.SelectedItem).Value;
        int bits = _bits.SelectedItem is int b ? b : 4096;
        var comment = _comment.Text.Trim();
        var pass = _pass1.Password.Length > 0 ? _pass1.Password : null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _key = await Task.Run(() => SshKeys.Generate(type, comment, pass, bits));
            _public.Text = _key.PublicKeyLine;
            _fingerprint.Text = _key.Fingerprint;
            _saveFile.IsEnabled = _saveVault.IsEnabled = _copy.IsEnabled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { MessageDialog.Error("Key generator", ex.Message); }
        finally { Mouse.OverrideCursor = null; }
    }

    private void SaveFiles()
    {
        if (_key == null) return;
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Directory.CreateDirectory(sshDir);
        var defaultName = _key.Type switch { SshKeyType.Ed25519 => "id_ed25519", SshKeyType.Rsa => "id_rsa", _ => "id_ecdsa" };
        var dlg = new Microsoft.Win32.SaveFileDialog { InitialDirectory = sshDir, FileName = defaultName, Title = "Save private key (the .pub file is written alongside)" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (File.Exists(dlg.FileName) && !MessageDialog.Confirm("Overwrite", $"{dlg.FileName} exists. Overwrite it?", "Overwrite", "Cancel", MessageKind.Warning)) return;
            File.WriteAllText(dlg.FileName, _key.PrivateKeyPem);
            FileProtection.RestrictToCurrentUser(dlg.FileName);
            File.WriteAllText(dlg.FileName + ".pub", _key.PublicKeyLine + "\n");
            MessageDialog.Info("Key saved", $"Private key: {dlg.FileName}\nPublic key: {dlg.FileName}.pub");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageDialog.Error("Save key", ex.Message); }
    }

    private void SaveVault()
    {
        if (_key == null) return;
        if (!UnlockVaultDialog.EnsureAvailable("Storing keys requires the encrypted vault.")) return;
        var name = PromptDialog.AskText("Store key", "A name for this key in the vault.", "Name", string.IsNullOrWhiteSpace(_comment.Text) ? _key.Type.ToString() : _comment.Text);
        if (string.IsNullOrWhiteSpace(name)) return;
        AppServices.Vault.SaveKey(new VaultKey
        {
            Name = name.Trim(), PrivateKey = _key.PrivateKeyPem, Passphrase = _pass1.Password.Length > 0 ? _pass1.Password : null,
            PublicKey = _key.PublicKeyLine, Fingerprint = _key.Fingerprint, Comment = _comment.Text.Trim(),
        });
        MessageDialog.Info("Key stored", "The key is stored in the vault. Select it in a session with Authentication = Public key.");
    }
}

/// <summary>Trusted host keys (TOFU database).</summary>
public sealed class KnownHostsWindow : DialogBase
{
    private readonly ListView _list = new() { Height = 360 };

    private KnownHostsWindow()
    {
        Title = "Known hosts";
        Width = 820;
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new TextBlock { Text = "Host keys you have trusted. Remove an entry to be asked again on the next connection.", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var remove = new Button { Content = "Remove selected" };
        remove.Click += (_, _) =>
        {
            foreach (var e in _list.SelectedItems.Cast<KnownHostEntry>().ToList()) AppServices.KnownHosts.Remove(e);
            Refresh();
        };
        bar.Children.Add(remove);
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 88 };
        bar.Children.Add(close);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Host", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostEntry.Host)), Width = 200 });
        gv.Columns.Add(new GridViewColumn { Header = "Port", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostEntry.Port)), Width = 60 });
        gv.Columns.Add(new GridViewColumn { Header = "Key type", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostEntry.KeyType)), Width = 140 });
        gv.Columns.Add(new GridViewColumn { Header = "SHA-256 fingerprint", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostEntry.FingerprintSha256)), Width = 330 });
        gv.Columns.Add(new GridViewColumn { Header = "Added", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostEntry.Added)) { StringFormat = "d" }, Width = 90 });
        _list.View = gv;
        root.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(2), Child = _list });
        Content = root;
        Refresh();
    }

    private void Refresh() => _list.ItemsSource = AppServices.KnownHosts.Entries.OrderBy(e => e.Host).ThenBy(e => e.Port).ToList();

    public static void Open() => new KnownHostsWindow().ShowDialog();
}

public sealed class AboutWindow : DialogBase
{
    private AboutWindow()
    {
        Title = "About SessionForge";
        Width = 460;
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        root.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/SessionForge;component/Assets/icon256.png")), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left });
        root.Children.Add(new TextBlock { Text = "SessionForge", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        var v = typeof(AboutWindow).Assembly.GetName().Version;
        root.Children.Add(new TextBlock { Text = $"Version {v?.ToString(3)}  •  .NET {Environment.Version}", Foreground = (Brush)FindResource("Fg.Muted") });
        root.Children.Add(new TextBlock
        {
            Text = "Remote session manager for SSH, SFTP, RDP, VNC, Telnet, serial and local terminals.\n\n" +
                   "Third-party components: SSH.NET (MIT), BouncyCastle (MIT), Konscious Argon2 (MIT), CommunityToolkit.Mvvm (MIT). " +
                   "Remote Desktop uses the Microsoft RDP client control that ships with Windows.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Foreground = (Brush)FindResource("Fg.Secondary"),
        });
        root.Children.Add(new TextBlock { Text = $"Data folder: {AppServices.Paths.Root}", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 14, 0, 0) });
        var ok = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        root.Children.Add(ok);
        Content = root;
    }

    public static void Open() => new AboutWindow().ShowDialog();
}
