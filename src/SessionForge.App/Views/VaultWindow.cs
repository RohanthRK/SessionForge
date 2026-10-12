using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Core.Security;
using SessionForge.Protocols;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Views;

/// <summary>Credential vault manager: stored credentials, SSH keys, saved secrets, master password and lock policy.</summary>
public sealed class VaultWindow : DialogBase
{
    private readonly ListBox _creds = new() { Height = 300 };
    private readonly ListBox _keys = new() { Height = 300 };
    private readonly ListBox _secrets = new() { Height = 300 };
    private DateTime _lastReauth = DateTime.MinValue;

    private VaultWindow()
    {
        Title = "Credential vault";
        Width = 720;
        var tabs = new TabControl { Style = (Style)FindResource("Tabs.Pill"), Margin = new Thickness(20, 16, 20, 0) };
        tabs.Items.Add(new TabItem { Header = "Credentials", Content = CredentialsTab() });
        tabs.Items.Add(new TabItem { Header = "SSH keys", Content = KeysTab() });
        tabs.Items.Add(new TabItem { Header = "Saved passwords", Content = SecretsTab() });
        tabs.Items.Add(new TabItem { Header = "Security", Content = SecurityTab() });
        var root = new DockPanel();
        var footer = new Border { Padding = new Thickness(20, 12, 20, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        footer.Child = close;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
        Refresh();
    }

    public static void Open()
    {
        if (!AppServices.Vault.Exists)
        {
            if (!CreateVaultDialog.Run()) return;
        }
        else if (!UnlockVaultDialog.EnsureUnlocked()) return;
        new VaultWindow().ShowDialog();
    }

    private void Refresh()
    {
        if (!AppServices.Vault.IsUnlocked) { Close(); return; }
        _creds.ItemsSource = AppServices.Vault.Credentials.OrderBy(c => c.Name).Select(c => new Option<VaultCredential>(c, $"{c.Name}   —   {c.Domain}{(string.IsNullOrEmpty(c.Domain) ? "" : "\\")}{c.Username}")).ToList();
        _keys.ItemsSource = AppServices.Vault.Keys.OrderBy(k => k.Name).Select(k => new Option<VaultKey>(k, $"{k.Name}   {k.Fingerprint}")).ToList();
        _secrets.ItemsSource = AppServices.Vault.SecretKeys.OrderBy(k => k).Select(k => new Option<string>(k, DescribeSecret(k))).ToList();
    }

    private static string DescribeSecret(string key)
    {
        var parts = key.Split(':');
        if (parts.Length >= 3 && parts[0] == "session" && Guid.TryParse(parts[1], out var id))
        {
            var name = AppServices.Sessions.Get(id)?.Name ?? "(deleted session)";
            return $"{name}  —  {string.Join(' ', parts.Skip(2))}";
        }
        return key;
    }

    private DockPanel WithButtons(Control list, params (string Label, Action Action)[] buttons)
    {
        var dock = new DockPanel();
        var panel = new StackPanel { Margin = new Thickness(10, 0, 0, 0), Width = 150 };
        foreach (var (label, action) in buttons)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 0, 6) };
            b.Click += (_, _) => action();
            panel.Children.Add(b);
        }
        DockPanel.SetDock(panel, Dock.Right);
        dock.Children.Add(panel);
        dock.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(4), Child = list });
        return dock;
    }

    #region Credentials

    private UIElement CredentialsTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Named credentials can be shared by many sessions (Session → Stored credential). Changing the password here updates all of them.", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(WithButtons(_creds,
            ("Add…", () => EditCredential(new VaultCredential())),
            ("Edit…", () => { if (Selected<VaultCredential>(_creds) is { } c && Reauth()) EditCredential(c); }),
            ("Copy username", () => { if (Selected<VaultCredential>(_creds) is { } c && c.Username != null) Clipboard.SetText(c.Username); }),
            ("Copy password", () => { if (Selected<VaultCredential>(_creds) is { Password: { } p } && Reauth()) CopySecret(p); }),
            ("Delete", DeleteCredential)));
        return sp;
    }

    private static T? Selected<T>(ListBox list) where T : class => (list.SelectedItem as Option<T>)?.Value;

    private void EditCredential(VaultCredential c)
    {
        var fields = new List<PromptField>
        {
            new("Name", false, c.Name), new("Username", false, c.Username), new("Domain (optional)", false, c.Domain), new("Password", true, c.Password),
        };
        var r = PromptDialog.Ask(string.IsNullOrEmpty(c.Name) ? "New credential" : "Edit credential", "Stored encrypted in the vault.", fields, offerSave: false);
        if (r == null) return;
        if (string.IsNullOrWhiteSpace(r.Values[0])) { MessageDialog.Error("Credential", "A name is required."); return; }
        c.Name = r.Values[0].Trim();
        c.Username = string.IsNullOrWhiteSpace(r.Values[1]) ? null : r.Values[1].Trim();
        c.Domain = string.IsNullOrWhiteSpace(r.Values[2]) ? null : r.Values[2].Trim();
        c.Password = r.Values[3];
        AppServices.Vault.SaveCredential(c);
        Refresh();
    }

    private void DeleteCredential()
    {
        if (Selected<VaultCredential>(_creds) is not { } c) return;
        int users = AppServices.Sessions.Sessions.Count(s => s.Credential.CredentialId == c.Id);
        if (!MessageDialog.Confirm("Delete credential", $"Delete '{c.Name}'?{(users > 0 ? $" {users} session(s) use it and will prompt for a password instead." : string.Empty)}", "Delete", "Cancel", MessageKind.Warning)) return;
        AppServices.Vault.DeleteCredential(c.Id);
        foreach (var s in AppServices.Sessions.Sessions.Where(s => s.Credential.CredentialId == c.Id).ToList())
        {
            s.Credential.CredentialId = null;
            AppServices.Sessions.AddOrUpdate(s);
        }
        Refresh();
    }

    #endregion

    #region Keys

    private UIElement KeysTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Keys stored here never touch the disk unencrypted. Select one in a session with Authentication = Public key.", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(WithButtons(_keys,
            ("Generate…", () => { KeyManagerWindow.Open(); Refresh(); }),
            ("Import file…", ImportKey),
            ("Copy public key", () => { if (Selected<VaultKey>(_keys) is { PublicKey: { } pk }) { Clipboard.SetText(pk); MessageDialog.Info("Public key", "Public key copied. Add it to ~/.ssh/authorized_keys on the server."); } }),
            ("Export private…", ExportKey),
            ("Delete", () => { if (Selected<VaultKey>(_keys) is { } k && MessageDialog.Confirm("Delete key", $"Delete '{k.Name}' from the vault?", "Delete", "Cancel", MessageKind.Warning)) { AppServices.Vault.DeleteKey(k.Id); Refresh(); } })));
        return sp;
    }

    private void ImportKey()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import private key" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = System.IO.File.ReadAllText(dlg.FileName);
            string? passphrase = null;
            var info = SshKeys.Inspect(text, null);
            if (info.NeedsPassphrase)
            {
                var r = PromptDialog.Ask("Key passphrase", "The key is encrypted. Enter its passphrase to verify it (it is stored with the key in the vault).", [new PromptField("Passphrase", true)], false);
                if (r == null) return;
                passphrase = r.Values[0];
                info = SshKeys.Inspect(text, passphrase);
            }
            if (info.Error != null) { MessageDialog.Error("Import key", info.Error); return; }
            AppServices.Vault.SaveKey(new VaultKey
            {
                Name = System.IO.Path.GetFileName(dlg.FileName), PrivateKey = text, Passphrase = passphrase,
                PublicKey = info.PublicKey, Fingerprint = info.Fingerprint, Comment = info.Comment,
            });
            Refresh();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { MessageDialog.Error("Import key", ex.Message); }
    }

    private void ExportKey()
    {
        if (Selected<VaultKey>(_keys) is not { } k || !Reauth()) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = k.Name, Title = "Export private key" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, k.PrivateKey);
            FileProtection.RestrictToCurrentUser(dlg.FileName);
            MessageDialog.Info("Export", "Private key exported. The file is readable only by your Windows account.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { MessageDialog.Error("Export", ex.Message); }
    }

    #endregion

    #region Secrets

    private UIElement SecretsTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Passwords and passphrases saved for individual sessions, jump hosts and proxies.", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(WithButtons(_secrets,
            ("Reveal…", () => { if (Selected<string>(_secrets) is { } k && Reauth()) MessageDialog.Show("Saved secret", DescribeSecret(k), MessageKind.Info, detail: AppServices.Vault.GetSecret(k)); }),
            ("Copy", () => { if (Selected<string>(_secrets) is { } k && Reauth() && AppServices.Vault.GetSecret(k) is { } v) CopySecret(v); }),
            ("Delete", () => { if (Selected<string>(_secrets) is { } k) { AppServices.Vault.SetSecret(k, null); Refresh(); } })));
        return sp;
    }

    #endregion

    #region Security

    private UIElement SecurityTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "ENCRYPTION", Style = (Style)FindResource("Text.Section"), Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(new TextBlock { Text = "AES-256-GCM with a random data key. The data key is wrapped with a key derived from your master password using Argon2id (64 MiB, 3 passes).", Style = (Style)FindResource("Text.Hint") });
        var remember = new CheckBox { Content = "Unlock automatically on this Windows account (DPAPI)", IsChecked = AppServices.Vault.RemembersOnThisComputer, Margin = new Thickness(0, 12, 0, 0) };
        remember.Click += (_, _) =>
        {
            if (!Reauth()) { remember.IsChecked = AppServices.Vault.RemembersOnThisComputer; return; }
            AppServices.Vault.SetRememberOnThisComputer(remember.IsChecked == true);
            AppServices.SettingsStore.Update(s => s.VaultUnlockPolicy = remember.IsChecked == true ? VaultUnlockPolicy.RememberOnThisComputer : VaultUnlockPolicy.EveryStartup);
        };
        sp.Children.Add(remember);
        var change = new Button { Content = "Change master password…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        change.Click += (_, _) => ChangePassword();
        sp.Children.Add(change);
        var lockNow = new Button { Content = "Lock vault now", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        lockNow.Click += (_, _) => { AppServices.Vault.Lock(); Close(); };
        sp.Children.Add(lockNow);
        sp.Children.Add(new TextBlock { Text = $"Auto-lock after {AppServices.Settings.VaultAutoLockMinutes} minutes of inactivity (change in Settings → Security).", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(0, 14, 0, 0) });
        return sp;
    }

    private void ChangePassword()
    {
        var r = PromptDialog.Ask("Change master password", "All secrets are re-protected with the new password.",
            [new PromptField("Current master password", true), new PromptField("New master password", true), new PromptField("Confirm new password", true)], false);
        if (r == null) return;
        if (r.Values[1].Length < 8) { MessageDialog.Error("Master password", "The new password must be at least 8 characters."); return; }
        if (r.Values[1] != r.Values[2]) { MessageDialog.Error("Master password", "The new passwords do not match."); return; }
        try
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            AppServices.Vault.ChangeMasterPassword(r.Values[0], r.Values[1]);
            MessageDialog.Info("Master password", "The master password was changed.");
        }
        catch (VaultAuthenticationException) { MessageDialog.Error("Master password", "The current master password is incorrect."); }
        finally { Mouse.OverrideCursor = null; }
    }

    #endregion

    /// <summary>Asks for the master password before revealing secrets (cached for 2 minutes).</summary>
    private bool Reauth()
    {
        if (!AppServices.Settings.RequireReauthToRevealPasswords) return true;
        if (DateTime.UtcNow - _lastReauth < TimeSpan.FromMinutes(2)) return true;
        var r = PromptDialog.Ask("Confirm your identity", "Enter the master password to view or copy secrets.", [new PromptField("Master password", true)], false);
        if (r == null) return false;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        bool ok;
        try { ok = AppServices.Vault.VerifyMasterPassword(r.Values[0]); }
        finally { Mouse.OverrideCursor = null; }
        if (!ok) { MessageDialog.Error("Vault", "Incorrect master password."); return false; }
        _lastReauth = DateTime.UtcNow;
        return true;
    }

    /// <summary>Copies a secret and clears the clipboard after the configured delay if it still holds the secret.</summary>
    public static void CopySecret(string secret)
    {
        try { Clipboard.SetText(secret); } catch (System.Runtime.InteropServices.COMException) { return; }
        int seconds = AppServices.Settings.ClipboardClearSeconds;
        if (seconds <= 0) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { }
        };
        timer.Start();
    }

    private static class Mouse
    {
        public static System.Windows.Input.Cursor? OverrideCursor { set => System.Windows.Input.Mouse.OverrideCursor = value; }
    }
}
