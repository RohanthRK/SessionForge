using System.Text;
using System.Text.RegularExpressions;
using SessionForge.Core.Automation;
using SessionForge.Core.Models;
using SessionForge.Protocols.Logging;
using SessionForge.Terminal;

namespace SessionForge.Protocols.Automation;

/// <summary>Where a macro sends input and observes output.</summary>
public interface IMacroTarget
{
    void SendText(string text);
    TerminalModes Modes { get; }
    /// <summary>Raised with decoded output text (escape sequences included).</summary>
    event Action<string>? OutputText;
}

public sealed class MacroException : Exception
{
    public MacroException(string message) : base(message) { }
}

/// <summary>
/// Executes macros against a target: typed text (with escapes), named keys, sleeps, wait-for-pattern on output,
/// secrets from the vault and nested macro calls (with recursion protection).
/// </summary>
public sealed class MacroRunner
{
    private readonly IMacroTarget _target;
    private readonly Func<string, Macro?> _findMacro;
    private readonly Func<string, string?> _resolveSecret;
    private const int MaxDepth = 8;

    public MacroRunner(IMacroTarget target, Func<string, Macro?> findMacro, Func<string, string?> resolveSecret)
    {
        _target = target;
        _findMacro = findMacro;
        _resolveSecret = resolveSecret;
    }

    public event Action<string>? Status;

    public Task RunAsync(Macro macro, CancellationToken ct) => RunAsync(macro, 0, ct);

    private async Task RunAsync(Macro macro, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth) throw new MacroException($"Macro nesting deeper than {MaxDepth} levels (recursive call?).");
        Status?.Invoke($"Running macro '{macro.Name}'");

        // Collect output continuously so WaitFor sees text that arrives between steps.
        var plain = new StringBuilder();
        var extractor = new PlainTextExtractor(line => { lock (plain) plain.Append(line).Append('\n'); });
        void OnOutput(string text)
        {
            lock (plain) { extractor.Feed(text); }
        }
        _target.OutputText += OnOutput;
        try
        {
            for (int i = 0; i < macro.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = macro.Steps[i];
                switch (step.Kind)
                {
                    case MacroStepKind.SendText:
                        await TypeAsync(MacroText.Unescape(step.Value), macro.KeystrokeDelayMs, ct).ConfigureAwait(false);
                        break;
                    case MacroStepKind.SendKey:
                        _target.SendText(KeyNames.Encode(step.Value, _target.Modes) ?? throw new MacroException($"Unknown key '{step.Value}' in step {i + 1}."));
                        break;
                    case MacroStepKind.Sleep:
                        await Task.Delay(Math.Clamp(step.TimeoutMs, 0, 3_600_000), ct).ConfigureAwait(false);
                        break;
                    case MacroStepKind.WaitFor:
                        await WaitForAsync(step, plain, extractor, i, ct).ConfigureAwait(false);
                        break;
                    case MacroStepKind.SendSecret:
                        var secret = _resolveSecret(step.Value) ?? throw new MacroException($"Secret '{step.Value}' not found (or vault locked).");
                        _target.SendText(secret);
                        break;
                    case MacroStepKind.CallMacro:
                        var other = _findMacro(step.Value) ?? throw new MacroException($"Macro '{step.Value}' not found.");
                        await RunAsync(other, depth + 1, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            _target.OutputText -= OnOutput;
        }
        Status?.Invoke($"Macro '{macro.Name}' finished");
    }

    private async Task TypeAsync(string text, int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0) { _target.SendText(text); return; }
        foreach (var ch in text)
        {
            _target.SendText(ch.ToString());
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForAsync(MacroStep step, StringBuilder plain, PlainTextExtractor extractor, int index, CancellationToken ct)
    {
        Regex rx;
        try { rx = new Regex(step.Value, RegexOptions.Multiline, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException ex) { throw new MacroException($"Invalid pattern in step {index + 1}: {ex.Message}"); }

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, step.TimeoutMs));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string snapshot;
            lock (plain)
            {
                // Include the in-progress line: prompts usually don't end with a newline.
                snapshot = plain.ToString() + extractor.PeekPartial();
            }
            var m = rx.Match(snapshot);
            if (m.Success)
            {
                lock (plain)
                {
                    // Consume through the match so the next WaitFor needs fresh output. A match inside the
                    // partial line consumes all completed lines; the partial line is kept by the extractor.
                    plain.Remove(0, Math.Min(plain.Length, m.Index + m.Length));
                    extractor.ConsumePartial(Math.Max(0, m.Index + m.Length - snapshot.Length + extractor.PeekPartial().Length));
                }
                return;
            }
            if (DateTime.UtcNow >= deadline)
            {
                if (step.FailOnTimeout) throw new MacroException($"Timed out waiting for /{step.Value}/ (step {index + 1}).");
                return;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Maps human key names ("Enter", "Ctrl+C", "Alt+F4", "Up", "F5", "Esc", "Tab") to terminal input.</summary>
public static class KeyNames
{
    public static string? Encode(string name, TerminalModes modes)
    {
        var parts = name.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var mods = KeyModifiers.None;
        foreach (var m in parts[..^1])
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Control; break;
                case "alt" or "meta": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                default: return null;
            }
        }
        var key = parts[^1];
        TerminalKey tk = key.ToLowerInvariant() switch
        {
            "enter" or "return" => TerminalKey.Enter,
            "backspace" or "bs" => TerminalKey.Backspace,
            "tab" => TerminalKey.Tab,
            "esc" or "escape" => TerminalKey.Escape,
            "up" => TerminalKey.Up, "down" => TerminalKey.Down, "left" => TerminalKey.Left, "right" => TerminalKey.Right,
            "home" => TerminalKey.Home, "end" => TerminalKey.End, "insert" or "ins" => TerminalKey.Insert,
            "delete" or "del" => TerminalKey.Delete, "pageup" or "pgup" => TerminalKey.PageUp, "pagedown" or "pgdn" => TerminalKey.PageDown,
            "f1" => TerminalKey.F1, "f2" => TerminalKey.F2, "f3" => TerminalKey.F3, "f4" => TerminalKey.F4,
            "f5" => TerminalKey.F5, "f6" => TerminalKey.F6, "f7" => TerminalKey.F7, "f8" => TerminalKey.F8,
            "f9" => TerminalKey.F9, "f10" => TerminalKey.F10, "f11" => TerminalKey.F11, "f12" => TerminalKey.F12,
            "space" => TerminalKey.None,
            _ => TerminalKey.None,
        };
        if (tk != TerminalKey.None) return InputEncoder.EncodeKey(tk, mods, modes);
        char c = key.Equals("space", StringComparison.OrdinalIgnoreCase) ? ' ' : key.Length == 1 ? key[0] : '\0';
        if (c == '\0') return null;
        return InputEncoder.EncodeChar(c, mods);
    }
}
