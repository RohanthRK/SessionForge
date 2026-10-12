using SessionForge.App.Dialogs;
using SessionForge.Core.Security;
using SessionForge.Protocols;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Services;

/// <summary>
/// Connects protocol-layer callbacks (host keys, prompts, secrets) to WPF dialogs and the credential vault.
/// Called from background connection threads; all UI work is marshalled to the dispatcher.
/// </summary>
public sealed class WpfInteraction : IConnectionInteraction, ISshSecretsProvider
{
    /// <summary>Raised for informational notices (banners etc.).</summary>
    public event Action<string>? Notice;

    public HostKeyDecision ConfirmHostKey(HostKeyInfo info) => HostKeyDialog.Ask(info);

    public string? GetStoredSecret(SecretTarget target)
    {
        if (!AppServices.Vault.Exists) return null;
        if (!UnlockVaultDialog.EnsureUnlocked($"Unlock the vault to use the saved credentials for {target.DisplayName}.")) return null;
        try { return AppServices.Vault.GetSecret(target.VaultKey); }
        catch (VaultLockedException) { return null; }
    }

    public void StoreSecret(SecretTarget target, string value)
    {
        if (!UnlockVaultDialog.EnsureAvailable("Saving passwords requires the encrypted credential vault.")) return;
        try
        {
            AppServices.Vault.SetSecret(target.VaultKey, value);
            MarkSaved(target.VaultKey);
        }
        catch (Exception ex)
        {
            Log.Error("Could not store secret", ex);
            MessageDialog.Error("Vault", "The password could not be saved: " + ex.Message);
        }
    }

    /// <summary>Flags the owning session/jump host as "password saved" so future connects look it up.</summary>
    private static void MarkSaved(string key)
    {
        // key forms: session:{guid}:password | session:{guid}:passphrase | session:{guid}:jump{n}:password
        var parts = key.Split(':');
        if (parts.Length < 3 || parts[0] != "session" || !Guid.TryParse(parts[1], out var id)) return;
        var s = AppServices.Sessions.Get(id);
        if (s == null) return;
        if (parts[2].StartsWith("jump", StringComparison.Ordinal) && int.TryParse(parts[2][4..], out int idx) && idx < s.Ssh.JumpHosts.Count)
        {
            if (s.Ssh.JumpHosts[idx].Credential.SavePassword) return;
            s.Ssh.JumpHosts[idx].Credential.SavePassword = true;
        }
        else if (parts[2] == "proxy")
        {
            if (s.Ssh.Proxy.HasPassword) return;
            s.Ssh.Proxy.HasPassword = true;
        }
        else
        {
            if (s.Credential.SavePassword) return;
            s.Credential.SavePassword = true;
        }
        AppServices.Sessions.AddOrUpdate(s);
    }

    public PromptResult? Prompt(string title, string message, IReadOnlyList<PromptField> fields, bool offerSave)
    {
        bool saveDefault = offerSave && AppServices.Vault.Exists;
        return PromptDialog.Ask(title, message, fields, offerSave, saveDefault);
    }

    public void Notify(string message)
    {
        Log.Info("Notice: " + message.Replace('\n', ' '));
        Notice?.Invoke(message);
    }

    public (string? Username, string? Password)? GetCredential(Guid credentialId)
    {
        if (!UnlockVaultDialog.EnsureUnlocked("Unlock the vault to use the selected stored credential.")) return null;
        var c = AppServices.Vault.GetCredential(credentialId);
        return c == null ? null : (c.Username, c.Password);
    }

    public (string PrivateKey, string? Passphrase)? GetVaultKey(Guid keyId)
    {
        if (!UnlockVaultDialog.EnsureUnlocked("Unlock the vault to use the stored SSH key.")) return null;
        var k = AppServices.Vault.GetKey(keyId);
        return k == null ? null : (k.PrivateKey, k.Passphrase);
    }
}
