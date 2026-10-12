namespace SessionForge.Core.Models;

public enum MacroStepKind
{
    /// <summary>Send text to the session (supports escapes like \r, \n, \t, \e, \xHH).</summary>
    SendText,
    /// <summary>Send a named key (e.g. "Enter", "Ctrl+C", "Up", "F5").</summary>
    SendKey,
    /// <summary>Pause for <see cref="MacroStep.TimeoutMs"/> milliseconds.</summary>
    Sleep,
    /// <summary>Wait until terminal output matches <see cref="MacroStep.Value"/> (regex), up to TimeoutMs.</summary>
    WaitFor,
    /// <summary>Run another macro by name.</summary>
    CallMacro,
    /// <summary>Send a password from the vault (by credential name or "session") without displaying it.</summary>
    SendSecret,
}

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 10_000;
    /// <summary>For WaitFor: abort macro (true) or continue (false) when the timeout expires.</summary>
    public bool FailOnTimeout { get; set; } = true;

    public override string ToString() => Kind switch
    {
        MacroStepKind.Sleep => $"Sleep {TimeoutMs} ms",
        MacroStepKind.WaitFor => $"Wait for /{Value}/ ({TimeoutMs} ms)",
        MacroStepKind.SendSecret => $"Send secret '{Value}'",
        _ => $"{Kind} {Value}",
    };
}

public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New macro";
    public string? Hotkey { get; set; }
    public int KeystrokeDelayMs { get; set; }
    public List<MacroStep> Steps { get; set; } = [];
}

public sealed class MacroDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<Macro> Macros { get; set; } = [];
}

/// <summary>Standalone SSH tunnel definition (managed independently from terminal tabs).</summary>
public sealed class TunnelProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New tunnel";
    /// <summary>SSH session providing the connection (host, credentials, jump hosts).</summary>
    public Guid SessionId { get; set; }
    public List<PortForward> Forwards { get; set; } = [];
    public bool AutoStart { get; set; }
    public bool AutoReconnect { get; set; } = true;
}

public sealed class TunnelDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<TunnelProfile> Tunnels { get; set; } = [];
}
