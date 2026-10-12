using System.Text.Json.Serialization;

namespace SessionForge.Core.Models;

public enum SessionKind { Ssh, Sftp, Telnet, Serial, LocalShell, Rdp, Vnc, Raw }

public enum AuthMethod { Password, PublicKey, KeyboardInteractive, Agent, None }

public enum ProxyKind { None, Socks4, Socks5, Http }

public enum ForwardKind { Local, Remote, Dynamic }

public enum SerialParity { None, Odd, Even, Mark, Space }
public enum SerialStopBits { One, OnePointFive, Two }
public enum SerialFlowControl { None, XonXoff, RtsCts, DsrDtr }

public enum LogMode { Off, PlainText, Raw, PlainTextWithTimestamps }

public enum TabStartMode { Normal, Detached, Fullscreen }

/// <summary>Reference to a secret in the vault, or an inline username with a vault-stored password.</summary>
public sealed class CredentialRef
{
    /// <summary>Id of a named credential in the vault (shared across sessions). Takes precedence.</summary>
    public Guid? CredentialId { get; set; }
    public string? Username { get; set; }
    /// <summary>When true the password is stored in the vault under the session's id.</summary>
    public bool SavePassword { get; set; }
}

public sealed class PortForward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ForwardKind Kind { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public uint BindPort { get; set; }
    public string TargetHost { get; set; } = "localhost";
    public uint TargetPort { get; set; }
    public bool Enabled { get; set; } = true;

    public override string ToString() => Kind switch
    {
        ForwardKind.Local => $"L {BindAddress}:{BindPort} → {TargetHost}:{TargetPort}",
        ForwardKind.Remote => $"R {BindAddress}:{BindPort} → {TargetHost}:{TargetPort}",
        _ => $"D SOCKS {BindAddress}:{BindPort}",
    };
}

public sealed class ProxySettings
{
    public ProxyKind Kind { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1080;
    public string? Username { get; set; }
    /// <summary>Proxy password lives in the vault under key "proxy:{sessionId}".</summary>
    public bool HasPassword { get; set; }
}

/// <summary>A jump (bastion) host hop. Either references another saved session or is inline.</summary>
public sealed class JumpHost
{
    public Guid? SessionId { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public CredentialRef Credential { get; set; } = new();
    public AuthMethod Auth { get; set; } = AuthMethod.Password;
    public string? PrivateKeyPath { get; set; }

    public override string ToString() => SessionId is not null ? $"session:{SessionId}" : $"{Credential.Username}@{Host}:{Port}";
}

public sealed class SshOptions
{
    public AuthMethod Auth { get; set; } = AuthMethod.Password;
    public string? PrivateKeyPath { get; set; }
    /// <summary>Id of a key stored in the vault's key store (alternative to a file path).</summary>
    public Guid? VaultKeyId { get; set; }
    public bool Compression { get; set; }
    public bool AgentForwarding { get; set; }
    public bool X11Forwarding { get; set; }
    public int KeepAliveSeconds { get; set; } = 30;
    public int ConnectTimeoutSeconds { get; set; } = 20;
    public string TerminalType { get; set; } = "xterm-256color";
    public string? RemoteCommand { get; set; }
    public bool StayAfterCommand { get; set; } = true;
    public string? StartupDirectory { get; set; }
    public bool OpenSftpBrowser { get; set; } = true;
    public bool FollowTerminalFolder { get; set; } = true;
    /// <summary>Show CPU/RAM/disk/load of Linux hosts under the terminal (polls via an exec channel).</summary>
    public bool RemoteMonitoring { get; set; }
    public List<JumpHost> JumpHosts { get; set; } = [];
    public List<PortForward> Forwards { get; set; } = [];
    public ProxySettings Proxy { get; set; } = new();
    /// <summary>Environment variables to send (subject to server AcceptEnv).</summary>
    public Dictionary<string, string> Environment { get; set; } = [];
    /// <summary>Optional algorithm preference overrides (empty = library defaults).</summary>
    public List<string> KexAlgorithms { get; set; } = [];
    public List<string> Ciphers { get; set; } = [];
    public List<string> HostKeyAlgorithms { get; set; } = [];
}

public sealed class TelnetOptions
{
    public string TerminalType { get; set; } = "xterm-256color";
    public bool ReturnSendsNewline { get; set; }
    public bool KeepAlive { get; set; }
}

public sealed class SerialOptions
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; }
    public SerialStopBits StopBits { get; set; }
    public SerialFlowControl FlowControl { get; set; }
    public bool LocalEcho { get; set; }
    /// <summary>Translate typed Enter into CR+LF instead of CR.</summary>
    public bool SendCrLf { get; set; }
}

public sealed class LocalShellOptions
{
    /// <summary>Executable + arguments, e.g. "powershell.exe -NoLogo", "cmd.exe", "wsl.exe -d Ubuntu".</summary>
    public string CommandLine { get; set; } = "powershell.exe -NoLogo";
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = [];
}

public sealed class RdpOptions
{
    public string? Domain { get; set; }
    public int Width { get; set; }            // 0 = fit to tab
    public int Height { get; set; }
    public int ColorDepth { get; set; } = 32;
    public bool SmartSizing { get; set; } = true;
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectSmartCards { get; set; }
    public bool RedirectPorts { get; set; }
    public int AudioMode { get; set; } // 0 = play locally, 1 = on remote, 2 = none
    public bool RedirectMicrophone { get; set; }
    public bool AdminConsole { get; set; }
    public int AuthenticationLevel { get; set; } = 2; // 0 none, 1 required, 2 prompt
    public bool EnableCredSsp { get; set; } = true;
    public bool Compression { get; set; } = true;
    public string? Gateway { get; set; }
    /// <summary>When true, the RDP connection is tunnelled through the SSH jump host chain via a local forward.</summary>
    public bool TunnelThroughSsh { get; set; }
}

public sealed class VncOptions
{
    public bool ViewOnly { get; set; }
    public bool Scale { get; set; } = true;
    public bool SharedSession { get; set; } = true;
    public bool SharedClipboard { get; set; } = true;
    /// <summary>Tunnel the VNC connection through the session's SSH jump host chain.</summary>
    public bool TunnelThroughSsh { get; set; }
}

public sealed class TerminalOptions
{
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? ThemeName { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public int ScrollbackLines { get; set; } = 10_000;
    public bool BackspaceSendsControlH { get; set; }
    public LogMode Logging { get; set; } = LogMode.Off;
    public string? LogDirectory { get; set; }
    /// <summary>Macro (by id) to run automatically once connected.</summary>
    public Guid? StartupMacroId { get; set; }
    public bool LockTitle { get; set; }
    /// <summary>Keyword highlighting override (null = use the global setting).</summary>
    public bool? KeywordHighlighting { get; set; }
    /// <summary>Delay between pasted lines in milliseconds (for slow serial devices / network gear). 0 = paste at once.</summary>
    public int PasteLineDelayMs { get; set; }
}

/// <summary>A saved connection definition (the equivalent of a bookmark in other tools).</summary>
public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New session";
    public Guid? FolderId { get; set; }
    public SessionKind Kind { get; set; } = SessionKind.Ssh;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public CredentialRef Credential { get; set; } = new();
    public string? Notes { get; set; }
    public string? TabColor { get; set; }
    public string? Icon { get; set; }
    public List<string> Tags { get; set; } = [];
    public TabStartMode StartMode { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastConnected { get; set; }
    public int SortOrder { get; set; }

    public SshOptions Ssh { get; set; } = new();
    public TelnetOptions Telnet { get; set; } = new();
    public SerialOptions Serial { get; set; } = new();
    public LocalShellOptions Shell { get; set; } = new();
    public RdpOptions Rdp { get; set; } = new();
    public VncOptions Vnc { get; set; } = new();
    public TerminalOptions Terminal { get; set; } = new();

    [JsonIgnore]
    public bool IsTerminal => Kind is SessionKind.Ssh or SessionKind.Telnet or SessionKind.Serial or SessionKind.LocalShell or SessionKind.Raw;

    public static int DefaultPort(SessionKind kind) => kind switch
    {
        SessionKind.Ssh or SessionKind.Sftp => 22,
        SessionKind.Telnet => 23,
        SessionKind.Rdp => 3389,
        SessionKind.Vnc => 5900,
        _ => 0,
    };

    public string Describe() => Kind switch
    {
        SessionKind.Serial => $"{Serial.PortName} @ {Serial.BaudRate}",
        SessionKind.LocalShell => Shell.CommandLine,
        _ => string.IsNullOrEmpty(Credential.Username) ? $"{Host}:{Port}" : $"{Credential.Username}@{Host}:{Port}",
    };
}

public sealed class SessionFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New folder";
    public Guid? ParentId { get; set; }
    public bool Expanded { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>The persisted session tree.</summary>
public sealed class SessionDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<SessionFolder> Folders { get; set; } = [];
    public List<Session> Sessions { get; set; } = [];
    public List<Guid> RecentSessionIds { get; set; } = [];
}
