using System.Text;

namespace SessionForge.Terminal;

public enum TerminalKey
{
    None, Enter, Backspace, Tab, Escape, Up, Down, Left, Right, Home, End, Insert, Delete, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    KeypadEnter, Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
    KeypadDecimal, KeypadAdd, KeypadSubtract, KeypadMultiply, KeypadDivide,
}

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }

public sealed class KeyboardOptions
{
    /// <summary>Backspace sends ^H (0x08) instead of DEL (0x7F).</summary>
    public bool BackspaceSendsControlH { get; set; }
    /// <summary>Alt+key sends ESC prefix ("meta sends escape").</summary>
    public bool AltSendsEscape { get; set; } = true;
}

/// <summary>Converts key presses to the byte sequences an xterm-compatible host expects.</summary>
public static class InputEncoder
{
    /// <summary>xterm modifier parameter: 1 + (shift?1) + (alt?2) + (ctrl?4).</summary>
    private static int ModParam(KeyModifiers m) => 1 + ((m & KeyModifiers.Shift) != 0 ? 1 : 0) + ((m & KeyModifiers.Alt) != 0 ? 2 : 0) + ((m & KeyModifiers.Control) != 0 ? 4 : 0);

    public static string? EncodeKey(TerminalKey key, KeyModifiers mods, TerminalModes modes, KeyboardOptions? options = null)
    {
        options ??= new KeyboardOptions();
        int mp = ModParam(mods);
        bool hasMods = mp > 1;
        string alt = (mods & KeyModifiers.Alt) != 0 && options.AltSendsEscape ? "\x1b" : string.Empty;

        string Cursor(char c) => hasMods ? $"\x1b[1;{mp}{c}" : (modes.ApplicationCursorKeys ? $"\x1bO{c}" : $"\x1b[{c}");
        string Tilde(int n) => hasMods ? $"\x1b[{n};{mp}~" : $"\x1b[{n}~";
        string Ss3(char c) => hasMods ? $"\x1b[1;{mp}{c}" : $"\x1bO{c}";

        switch (key)
        {
            case TerminalKey.Enter:
            case TerminalKey.KeypadEnter when !modes.ApplicationKeypad:
                return alt + (modes.LineFeedNewLine ? "\r\n" : "\r");
            case TerminalKey.Backspace:
                {
                    bool ctrl = (mods & KeyModifiers.Control) != 0;
                    // Ctrl inverts the configured choice (common convention).
                    bool sendH = options.BackspaceSendsControlH ^ ctrl;
                    return alt + (sendH ? "\b" : "\x7f");
                }
            case TerminalKey.Tab: return (mods & KeyModifiers.Shift) != 0 ? "\x1b[Z" : alt + "\t";
            case TerminalKey.Escape: return alt + "\x1b";
            case TerminalKey.Up: return Cursor('A');
            case TerminalKey.Down: return Cursor('B');
            case TerminalKey.Right: return Cursor('C');
            case TerminalKey.Left: return Cursor('D');
            case TerminalKey.Home: return Cursor('H');
            case TerminalKey.End: return Cursor('F');
            case TerminalKey.Insert: return Tilde(2);
            case TerminalKey.Delete: return Tilde(3);
            case TerminalKey.PageUp: return Tilde(5);
            case TerminalKey.PageDown: return Tilde(6);
            case TerminalKey.F1: return Ss3('P');
            case TerminalKey.F2: return Ss3('Q');
            case TerminalKey.F3: return Ss3('R');
            case TerminalKey.F4: return Ss3('S');
            case TerminalKey.F5: return Tilde(15);
            case TerminalKey.F6: return Tilde(17);
            case TerminalKey.F7: return Tilde(18);
            case TerminalKey.F8: return Tilde(19);
            case TerminalKey.F9: return Tilde(20);
            case TerminalKey.F10: return Tilde(21);
            case TerminalKey.F11: return Tilde(23);
            case TerminalKey.F12: return Tilde(24);
        }

        if (key >= TerminalKey.KeypadEnter)
        {
            if (modes.ApplicationKeypad)
            {
                char c = key switch
                {
                    TerminalKey.KeypadEnter => 'M', TerminalKey.KeypadMultiply => 'j', TerminalKey.KeypadAdd => 'k',
                    TerminalKey.KeypadSubtract => 'm', TerminalKey.KeypadDecimal => 'n', TerminalKey.KeypadDivide => 'o',
                    _ => (char)('p' + (key - TerminalKey.Keypad0)),
                };
                return "\x1bO" + c;
            }
            string s = key switch
            {
                TerminalKey.KeypadMultiply => "*", TerminalKey.KeypadAdd => "+", TerminalKey.KeypadSubtract => "-",
                TerminalKey.KeypadDecimal => ".", TerminalKey.KeypadDivide => "/",
                _ => ((char)('0' + (key - TerminalKey.Keypad0))).ToString(),
            };
            return alt + s;
        }
        return null;
    }

    /// <summary>Encodes a typed character with Ctrl/Alt modifiers. Returns null when the combination produces nothing.</summary>
    public static string? EncodeChar(char c, KeyModifiers mods, KeyboardOptions? options = null)
    {
        options ??= new KeyboardOptions();
        string prefix = (mods & KeyModifiers.Alt) != 0 && options.AltSendsEscape ? "\x1b" : string.Empty;
        if ((mods & KeyModifiers.Control) != 0)
        {
            char? ctl = c switch
            {
                >= 'a' and <= 'z' => (char)(c - 'a' + 1),
                >= 'A' and <= 'Z' => (char)(c - 'A' + 1),
                '@' or ' ' or '2' => '\0',
                '[' or '3' => '\x1b',
                '\\' or '4' => '\x1c',
                ']' or '5' => '\x1d',
                '^' or '6' => '\x1e',
                '_' or '-' or '7' => '\x1f',
                '?' or '8' => '\x7f',
                _ => null,
            };
            if (ctl is null) return null;
            return prefix + ctl.Value;
        }
        return prefix + c;
    }

    /// <summary>Wraps pasted text for bracketed paste and normalises newlines to CR.</summary>
    public static string EncodePaste(string text, TerminalModes modes)
    {
        var normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!modes.BracketedPaste) return normalized;
        // Strip any embedded end-bracket sequence so pasted content can't escape bracketed mode.
        normalized = normalized.Replace("\x1b[201~", string.Empty);
        return "\x1b[200~" + normalized + "\x1b[201~";
    }

    public static string EncodeFocus(bool focused) => focused ? "\x1b[I" : "\x1b[O";
}

public enum MouseButton { Left = 0, Middle = 1, Right = 2, None = 3, WheelUp = 64, WheelDown = 65 }
public enum MouseAction { Press, Release, Move }

public static class MouseEncoder
{
    /// <summary>
    /// Encodes a mouse event for the host according to tracking mode/encoding. Coordinates are 0-based cells.
    /// Returns null when the event shouldn't be reported in the current mode.
    /// </summary>
    public static string? Encode(MouseButton button, MouseAction action, int col, int row, KeyModifiers mods, TerminalModes modes, bool anyButtonDown)
    {
        var mode = modes.MouseTracking;
        if (mode == MouseTrackingMode.None) return null;
        bool wheel = button is MouseButton.WheelUp or MouseButton.WheelDown;
        if (mode == MouseTrackingMode.X10 && (action != MouseAction.Press)) return null;
        if (action == MouseAction.Move)
        {
            if (mode == MouseTrackingMode.Normal) return null;
            if (mode == MouseTrackingMode.ButtonEvent && !anyButtonDown) return null;
        }
        if (wheel && action == MouseAction.Release) return null;

        int cb = (int)button;
        if (action == MouseAction.Release && modes.MouseEncoding != MouseEncoding.Sgr) cb = 3;
        if (mode != MouseTrackingMode.X10)
        {
            if ((mods & KeyModifiers.Shift) != 0) cb += 4;
            if ((mods & KeyModifiers.Alt) != 0) cb += 8;
            if ((mods & KeyModifiers.Control) != 0) cb += 16;
        }
        if (action == MouseAction.Move) cb += 32;

        int x = col + 1, y = row + 1;
        switch (modes.MouseEncoding)
        {
            case MouseEncoding.Sgr:
                return $"\x1b[<{cb};{x};{y}{(action == MouseAction.Release ? 'm' : 'M')}";
            case MouseEncoding.Urxvt:
                return $"\x1b[{cb + 32};{x};{y}M";
            case MouseEncoding.Utf8:
                {
                    var sb = new StringBuilder("\x1b[M");
                    sb.Append((char)(cb + 32));
                    sb.Append((char)Math.Min(x + 32, 2047));
                    sb.Append((char)Math.Min(y + 32, 2047));
                    return sb.ToString();
                }
            default:
                if (x > 223 || y > 223) return null; // not representable in X10 encoding
                return "\x1b[M" + (char)(cb + 32) + (char)(x + 32) + (char)(y + 32);
        }
    }
}
