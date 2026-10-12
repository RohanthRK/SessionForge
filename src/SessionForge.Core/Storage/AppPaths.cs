namespace SessionForge.Core.Storage;

/// <summary>
/// Resolves where SessionForge keeps its data. Portable mode (a file named "portable" next to the executable)
/// stores everything in a "Data" folder beside the app; otherwise %APPDATA%\SessionForge is used.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }

    public string Root { get; }
    public string SessionsFile => Path.Combine(Root, "sessions.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string MacrosFile => Path.Combine(Root, "macros.json");
    public string TunnelsFile => Path.Combine(Root, "tunnels.json");
    public string KnownHostsFile => Path.Combine(Root, "known_hosts.json");
    public string VaultFile => Path.Combine(Root, "vault.sfv");
    public string LogDirectory => Path.Combine(Root, "Logs");
    public string BackupDirectory => Path.Combine(Root, "Backups");
    public string DiagnosticsLog => Path.Combine(Root, "sessionforge.log");

    public static AppPaths Resolve()
    {
        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable")))
            return new AppPaths(Path.Combine(baseDir, "Data"));
        var env = Environment.GetEnvironmentVariable("SESSIONFORGE_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return new AppPaths(env);
        return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionForge"));
    }
}
