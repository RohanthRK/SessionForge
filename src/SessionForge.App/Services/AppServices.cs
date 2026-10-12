using SessionForge.Core.Models;
using SessionForge.Core.Security;
using SessionForge.Core.Storage;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Services;

/// <summary>Application-wide services, created once at startup.</summary>
public static class AppServices
{
    public static AppPaths Paths { get; private set; } = null!;
    public static DocumentStore<AppSettings> SettingsStore { get; private set; } = null!;
    public static AppSettings Settings => SettingsStore.Value;
    public static SessionStore Sessions { get; private set; } = null!;
    public static DocumentStore<MacroDocument> Macros { get; private set; } = null!;
    public static DocumentStore<TunnelDocument> Tunnels { get; private set; } = null!;
    public static KnownHostsStore KnownHosts { get; private set; } = null!;
    public static CredentialVault Vault { get; private set; } = null!;
    public static WpfInteraction Interaction { get; private set; } = null!;

    public static void Initialize()
    {
        Paths = AppPaths.Resolve();
        Log.Initialize(Paths.DiagnosticsLog);
        SettingsStore = new DocumentStore<AppSettings>(Paths.SettingsFile);
        Sessions = new SessionStore(Paths.SessionsFile);
        Macros = new DocumentStore<MacroDocument>(Paths.MacrosFile);
        Tunnels = new DocumentStore<TunnelDocument>(Paths.TunnelsFile);
        KnownHosts = new KnownHostsStore(Paths.KnownHostsFile);
        Vault = new CredentialVault(Paths.VaultFile) { AutoLockAfter = TimeSpan.FromMinutes(Math.Max(0, Settings.VaultAutoLockMinutes)) };
        Vault.StartAutoLock();
        Interaction = new WpfInteraction();
        BackupConfiguration();
    }

    /// <summary>Creates a connector bound to the UI prompts, vault and known hosts.</summary>
    public static SshConnector CreateConnector() => new(Interaction, Interaction, KnownHosts)
    {
        StrictHostKeyChecking = Settings.StrictHostKeyChecking,
    };

    public static Session? ResolveSession(Guid id) => Sessions.Get(id);

    /// <summary>Keeps a dated copy of the configuration files (last 10), excluding the vault's plaintext — the vault file is already encrypted.</summary>
    private static void BackupConfiguration()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            var dir = System.IO.Path.Combine(Paths.BackupDirectory, stamp);
            if (System.IO.Directory.Exists(dir)) return;
            System.IO.Directory.CreateDirectory(dir);
            foreach (var f in new[] { Paths.SessionsFile, Paths.SettingsFile, Paths.MacrosFile, Paths.TunnelsFile, Paths.KnownHostsFile, Paths.VaultFile })
                if (System.IO.File.Exists(f)) System.IO.File.Copy(f, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f)), overwrite: true);
            var all = System.IO.Directory.GetDirectories(Paths.BackupDirectory).OrderByDescending(d => d).Skip(10);
            foreach (var old in all) System.IO.Directory.Delete(old, recursive: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Configuration backup failed: {ex.Message}");
        }
    }
}
