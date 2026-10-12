namespace SessionForge.Core.Models;

public enum AppTheme { Dark, Light, System }
public enum VaultUnlockPolicy { EveryStartup, RememberOnThisComputer }
public enum PasteWarning { Never, MultiLine, Always }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string TerminalFontFamily { get; set; } = "Cascadia Mono, Consolas, Courier New";
    public double TerminalFontSize { get; set; } = 13;
    public string TerminalTheme { get; set; } = "Forge Dark";
    public string CursorStyle { get; set; } = "Block";
    public bool CursorBlink { get; set; } = true;
    public bool ShowSidebar { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;
    public bool ShowStatusBar { get; set; } = true;

    // Terminal behaviour
    public int ScrollbackLines { get; set; } = 10_000;
    public bool CopyOnSelect { get; set; } = true;
    public bool RightClickPastes { get; set; } = true;
    public PasteWarning PasteWarning { get; set; } = PasteWarning.MultiLine;
    public bool BellSound { get; set; }
    public bool BellFlash { get; set; } = true;
    public string WordDelimiters { get; set; } = " \t\"'`()[]{}<>|;,&";
    public bool AllowOsc52Clipboard { get; set; }
    public bool KeywordHighlighting { get; set; }
    public bool ConfirmCloseConnectedTab { get; set; } = true;
    public bool ReconnectPrompt { get; set; } = true;

    // Logging
    public LogMode DefaultLogMode { get; set; } = LogMode.Off;
    public string? LogDirectory { get; set; }
    public string LogFileNamePattern { get; set; } = "{session}_{date:yyyyMMdd_HHmmss}.log";

    // SSH
    public bool StrictHostKeyChecking { get; set; } = true;
    public int DefaultKeepAliveSeconds { get; set; } = 30;
    public bool AutoOpenSftp { get; set; } = true;
    public bool UseWindowsSshAgent { get; set; } = true;
    public bool PreserveFileTimesOnTransfer { get; set; } = true;
    public int MaxConcurrentTransfers { get; set; } = 3;

    // Security
    public VaultUnlockPolicy VaultUnlockPolicy { get; set; } = VaultUnlockPolicy.EveryStartup;
    public int VaultAutoLockMinutes { get; set; } = 30;
    public bool RequireReauthToRevealPasswords { get; set; } = true;
    public int ClipboardClearSeconds { get; set; } = 30;

    // Startup / workspace
    public bool RestoreSessionsOnStartup { get; set; }
    public List<Guid> LastOpenSessions { get; set; } = [];
    public bool CheckForUpdates { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    public Dictionary<string, string> KeyBindings { get; set; } = [];
}
