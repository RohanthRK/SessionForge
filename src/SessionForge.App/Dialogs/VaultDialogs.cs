using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Core.Security;

namespace SessionForge.App.Dialogs;

/// <summary>Creates the credential vault (master password + policy).</summary>
public sealed class CreateVaultDialog : DialogBase
{
    private readonly PasswordBox _pw1 = new();
    private readonly PasswordBox _pw2 = new();
    private readonly CheckBox _remember = new() { Content = "Unlock automatically on this Windows account (DPAPI)", Margin = new Thickness(0, 14, 0, 0) };
    private readonly ProgressBar _meter = new() { Maximum = 4, Height = 5, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _strength = new() { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

    private CreateVaultDialog(string? reason)
    {
        Title = "Create credential vault";
        Width = 480;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "Protect your saved passwords", Style = (Style)FindResource("Text.Title") });
        root.Children.Add(new TextBlock
        {
            Text = (reason != null ? reason + "\n\n" : string.Empty) +
                   "Passwords, passphrases and keys are encrypted with AES-256-GCM using a key derived from your master password (Argon2id). " +
                   "If you forget the master password, the saved secrets cannot be recovered.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14), Foreground = (Brush)FindResource("Fg.Secondary"),
        });
        root.Children.Add(new TextBlock { Text = "Master password", Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_pw1);
        root.Children.Add(_meter);
        root.Children.Add(_strength);
        root.Children.Add(new TextBlock { Text = "Confirm master password", Margin = new Thickness(0, 10, 0, 4) });
        root.Children.Add(_pw2);
        _remember.IsChecked = AppServices.Settings.VaultUnlockPolicy == VaultUnlockPolicy.RememberOnThisComputer;
        root.Children.Add(_remember);
        root.Children.Add(new TextBlock { Text = "When enabled, anyone who can sign in to this Windows account can open the vault without the master password.", Style = (Style)FindResource("Text.Hint"), Margin = new Thickness(26, 2, 0, 0) });
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Create vault", IsDefault = true, Style = (Style)FindResource("Button.Primary") };
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);
        Content = root;

        _pw1.PasswordChanged += (_, _) => UpdateStrength();
        UpdateStrength();
        Loaded += (_, _) => _pw1.Focus();
    }

    private void UpdateStrength()
    {
        var (level, bits) = PasswordStrength.Evaluate(_pw1.Password);
        _meter.Value = (int)level;
        _meter.SetResourceReference(ForegroundProperty, level switch { StrengthLevel.VeryWeak or StrengthLevel.Weak => "Danger", StrengthLevel.Fair => "Warning", _ => "Success" });
        _strength.Text = _pw1.Password.Length == 0 ? "Use at least 12 characters; a passphrase of several words works well." : $"Strength: {level} (~{bits:0} bits)";
        _strength.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Muted");
    }

    private void Submit()
    {
        var p = _pw1.Password;
        if (p.Length < 8) { _error.Text = "The master password must be at least 8 characters."; return; }
        if (p != _pw2.Password) { _error.Text = "The passwords do not match."; return; }
        if (PasswordStrength.Evaluate(p).Level == StrengthLevel.VeryWeak &&
            !MessageDialog.Confirm("Weak password", "This master password is very weak and could be guessed. Use it anyway?", "Use anyway", "Go back", MessageKind.Warning))
            return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            AppServices.Vault.Create(p, _remember.IsChecked == true);
            AppServices.SettingsStore.Update(s => s.VaultUnlockPolicy = _remember.IsChecked == true ? VaultUnlockPolicy.RememberOnThisComputer : VaultUnlockPolicy.EveryStartup);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    public static bool Run(string? reason = null) => OnUi(() => new CreateVaultDialog(reason).ShowDialog() == true);
}

/// <summary>Unlocks the vault with the master password.</summary>
public sealed class UnlockVaultDialog : DialogBase
{
    private readonly PasswordBox _pw = new();
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private int _attempts;

    private UnlockVaultDialog(string? reason)
    {
        Title = "Unlock credential vault";
        Width = 440;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = "", FontSize = 22, Margin = new Thickness(0, 0, 12, 0), Style = (Style)FindResource("Icon") };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = "Credential vault is locked", Style = (Style)FindResource("Text.Title"), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);
        root.Children.Add(new TextBlock
        {
            Text = reason ?? "Enter your master password to access saved passwords and keys.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14), Foreground = (Brush)FindResource("Fg.Secondary"),
        });
        root.Children.Add(_pw);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        root.Children.Add(_error);

        var bar = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var forgot = new Button { Content = "Forgot password…", Style = (Style)FindResource("Button.Ghost") };
        forgot.Click += (_, _) => Reset();
        bar.Children.Add(forgot);
        var cancel = new Button { Content = "Not now", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(cancel, Dock.Right);
        var ok = new Button { Content = "Unlock", IsDefault = true, Style = (Style)FindResource("Button.Primary") };
        DockPanel.SetDock(ok, Dock.Right);
        ok.Click += (_, _) => Submit();
        bar.Children.Add(cancel);
        bar.Children.Add(ok);
        root.Children.Add(bar);
        Content = root;
        Loaded += (_, _) => _pw.Focus();
    }

    private void Submit()
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            AppServices.Vault.Unlock(_pw.Password);
            DialogResult = true;
        }
        catch (VaultAuthenticationException ex)
        {
            _attempts++;
            // Progressive delay discourages guessing (Argon2id already costs ~0.5 s per attempt).
            if (_attempts >= 3) Thread.Sleep(Math.Min(5000, 500 * _attempts));
            _error.Text = ex.Message;
            _pw.SelectAll();
            _pw.Focus();
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void Reset()
    {
        if (!MessageDialog.Confirm("Reset vault",
                "Without the master password the stored secrets cannot be decrypted. Resetting deletes ALL saved passwords, passphrases and keys (a backup copy of the encrypted file is kept). Sessions themselves are not affected.\n\nReset the vault?",
                "Delete all secrets", "Cancel", MessageKind.Warning))
            return;
        AppServices.Vault.Destroy();
        DialogResult = false;
        CreateVaultDialog.Run("The vault was reset. Create a new master password.");
    }

    /// <summary>Ensures the vault is unlocked, prompting if necessary. Returns false if there is no vault or the user declined.</summary>
    public static bool EnsureUnlocked(string? reason = null)
    {
        var v = AppServices.Vault;
        if (!v.Exists) return false;
        if (v.IsUnlocked) { v.Activity(); return true; }
        if (v.TryUnlockWithWindows()) return true;
        return OnUi(() => new UnlockVaultDialog(reason).ShowDialog() == true);
    }

    /// <summary>Ensures a vault exists (offering to create one) and is unlocked.</summary>
    public static bool EnsureAvailable(string reason)
    {
        if (!AppServices.Vault.Exists && !CreateVaultDialog.Run(reason)) return false;
        return EnsureUnlocked(reason);
    }
}
