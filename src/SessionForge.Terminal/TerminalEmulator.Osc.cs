using System.Globalization;
using System.Text;

namespace SessionForge.Terminal;

public sealed partial class TerminalEmulator
{
    private const int MaxTitleLength = 4096;
    private const int MaxLinks = 4096;

    public void OscDispatch(string data)
    {
        int semi = data.IndexOf(';');
        string cmd = semi < 0 ? data : data[..semi];
        string arg = semi < 0 ? string.Empty : data[(semi + 1)..];
        if (!int.TryParse(cmd, NumberStyles.None, CultureInfo.InvariantCulture, out int code)) return;

        switch (code)
        {
            case 0:
            case 2:
                Title = Sanitize(arg);
                _titleDirty = true;
                break;
            case 1: break; // icon name
            case 4: OscPalette(arg); break;
            case 7: OscCwd(arg); break;
            case 8: OscHyperlink(arg); break;
            case 10: OscDynamicColor(10, arg); break;
            case 11: OscDynamicColor(11, arg); break;
            case 12: OscDynamicColor(12, arg); break;
            case 52: OscClipboard(arg); break;
            case 104: // reset palette
                {
                    var def = TerminalTheme.BuiltIn.FirstOrDefault(t => t.Name == Theme.Name);
                    if (def != null) Theme.Ansi = (Rgb[])def.Ansi.Clone();
                    break;
                }
        }
    }

    private static string Sanitize(string s)
    {
        if (s.Length > MaxTitleLength) s = s[..MaxTitleLength];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (ch >= 0x20 && ch != 0x7F) sb.Append(ch);
        return sb.ToString();
    }

    private void OscCwd(string arg)
    {
        // file://host/path (percent-encoded)
        if (!arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return;
        int slash = arg.IndexOf('/', 7);
        if (slash < 0) return;
        string path;
        try { path = Uri.UnescapeDataString(arg[slash..]); } catch (UriFormatException) { return; }
        WorkingDirectory = path;
        _cwdDirty = true;
    }

    private void OscHyperlink(string arg)
    {
        // OSC 8 ; params ; URI
        int semi = arg.IndexOf(';');
        if (semi < 0) return;
        string uri = arg[(semi + 1)..];
        if (uri.Length == 0 || uri.Length > 2048)
        {
            _attr = _attr with { LinkId = 0 };
            return;
        }
        if (!_linkIds.TryGetValue(uri, out int id))
        {
            if (_links.Count >= MaxLinks) { _links.Clear(); _linkIds.Clear(); }
            id = _nextLinkId++;
            _links[id] = uri;
            _linkIds[uri] = id;
        }
        _attr = _attr with { LinkId = id };
    }

    private void OscClipboard(string arg)
    {
        int semi = arg.IndexOf(';');
        if (semi < 0) return;
        string payload = arg[(semi + 1)..];
        if (payload == "?") return; // clipboard reads are never allowed (data exfiltration)
        if (!AllowClipboardWrite) return;
        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length > 1 << 20) return;
            _pendingClipboard = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException) { }
    }

    private void OscPalette(string arg)
    {
        var parts = arg.Split(';');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int idx) || idx < 0 || idx > 255) continue;
            if (parts[i + 1] == "?")
            {
                var c = Theme.Palette(idx);
                Respond($"\x1b]4;{idx};{XColor(c)}\x1b\\");
            }
            else if (idx < 16 && TryParseXColor(parts[i + 1], out var rgb))
            {
                Theme.Ansi[idx] = rgb;
            }
        }
    }

    private void OscDynamicColor(int code, string arg)
    {
        if (arg == "?")
        {
            var c = code switch { 10 => Theme.Foreground, 11 => Theme.Background, _ => Theme.Cursor };
            Respond($"\x1b]{code};{XColor(c)}\x1b\\");
        }
        else if (TryParseXColor(arg, out var rgb))
        {
            if (code == 10) Theme.Foreground = rgb;
            else if (code == 11) Theme.Background = rgb;
            else Theme.Cursor = rgb;
        }
    }

    private static string XColor(Rgb c) => $"rgb:{c.R:x2}{c.R:x2}/{c.G:x2}{c.G:x2}/{c.B:x2}{c.B:x2}";

    internal static bool TryParseXColor(string s, out Rgb rgb)
    {
        rgb = default;
        if (s.StartsWith('#') && s.Length == 7)
        {
            try { rgb = Rgb.FromHex(s); return true; } catch (FormatException) { return false; }
        }
        if (s.StartsWith("rgb:", StringComparison.Ordinal))
        {
            var parts = s[4..].Split('/');
            if (parts.Length != 3) return false;
            byte Comp(string h)
            {
                int v = int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return h.Length switch { 1 => (byte)(v * 17), 2 => (byte)v, 3 => (byte)(v >> 4), _ => (byte)(v >> 8) };
            }
            try { rgb = new Rgb(Comp(parts[0]), Comp(parts[1]), Comp(parts[2])); return true; }
            catch (FormatException) { return false; }
        }
        return false;
    }

    public void DcsDispatch(char priv, CsiParams p, string inter, char final, string data)
    {
        // DECRQSS: request status string
        if (inter == "$" && final == 'q')
        {
            string? reply = data switch
            {
                "m" => SgrString() + "m",
                "r" => $"{_scrollTop + 1};{_scrollBottom + 1}r",
                " q" => $"{CursorStyleCode()} q",
                _ => null,
            };
            Respond(reply == null ? "\x1bP0$r\x1b\\" : $"\x1bP1$r{reply}\x1b\\");
            return;
        }
        // XTGETTCAP: we don't publish a termcap database; answer "invalid" so apps fall back to terminfo.
        if (inter == "+" && final == 'q') Respond("\x1bP0+r\x1b\\");
    }

    private int CursorStyleCode() => Modes.CursorShape switch
    {
        CursorShape.Block => Modes.CursorBlink ? 1 : 2,
        CursorShape.Underline => Modes.CursorBlink ? 3 : 4,
        _ => Modes.CursorBlink ? 5 : 6,
    };

    private string SgrString()
    {
        var parts = new List<string> { "0" };
        var f = _attr.Flags;
        if ((f & CellFlags.Bold) != 0) parts.Add("1");
        if ((f & CellFlags.Dim) != 0) parts.Add("2");
        if ((f & CellFlags.Italic) != 0) parts.Add("3");
        if ((f & CellFlags.Underline) != 0) parts.Add("4");
        if ((f & CellFlags.Blink) != 0) parts.Add("5");
        if ((f & CellFlags.Inverse) != 0) parts.Add("7");
        if ((f & CellFlags.Hidden) != 0) parts.Add("8");
        if ((f & CellFlags.Strikethrough) != 0) parts.Add("9");
        static string? Col(TerminalColor c, int b) => c.Kind switch
        {
            ColorKind.Indexed when c.Index < 8 => (b + c.Index).ToString(CultureInfo.InvariantCulture),
            ColorKind.Indexed when c.Index < 16 => (b + 60 + c.Index - 8).ToString(CultureInfo.InvariantCulture),
            ColorKind.Indexed => $"{b + 8};5;{c.Index}",
            ColorKind.Rgb => $"{b + 8};2;{c.R};{c.G};{c.B}",
            _ => null,
        };
        if (Col(_attr.Foreground, 30) is { } fg) parts.Add(fg);
        if (Col(_attr.Background, 40) is { } bg) parts.Add(bg);
        return string.Join(';', parts);
    }
}
