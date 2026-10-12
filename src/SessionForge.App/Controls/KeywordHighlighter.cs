using System.Text.RegularExpressions;
using SessionForge.Terminal;

namespace SessionForge.App.Controls;

/// <summary>
/// Optional keyword colouring for terminal output (errors red, warnings yellow, success green, addresses cyan).
/// Only cells that use the default foreground are recoloured, so applications' own colours always win.
/// </summary>
public static partial class KeywordHighlighter
{
    [GeneratedRegex(@"\b(?:error|errors|fail|failed|failure|fatal|critical|crit|denied|refused|panic|exception|unreachable|timeout|timed out|abort(?:ed)?|segfault)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRx();

    [GeneratedRegex(@"\b(?:warn|warning|warnings|deprecated|retry(?:ing)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarnRx();

    [GeneratedRegex(@"\b(?:ok|success|successful|successfully|succeeded|passed|done|active|running|enabled|up|online|connected|accepted)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OkRx();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:/\d{1,2})?\b|\b(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRx();

    public const uint Red = 0xFFF0626A, Yellow = 0xFFE5C07B, Green = 0xFF8FD18A, Cyan = 0xFF6CC8D6;

    /// <summary>Returns per-column colour overrides (0 = none), or null when nothing matched.</summary>
    public static uint[]? Compute(TerminalLine line, int columns)
    {
        int n = Math.Min(columns, line.Length);
        var chars = new char[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            ref readonly var c = ref line[i];
            chars[i] = c.Width == 0 ? '\0' : c.Rune is > 0 and < 0x10000 ? (char)c.Rune : ' ';
            if (c.Rune > ' ') any = true;
        }
        if (!any) return null;
        var text = new string(chars);
        uint[]? result = null;
        void Apply(Regex rx, uint color)
        {
            foreach (Match m in rx.Matches(text))
            {
                result ??= new uint[n];
                for (int i = m.Index; i < m.Index + m.Length && i < n; i++)
                    if (line[i].Attributes.Foreground.IsDefault && result[i] == 0) result[i] = color;
            }
        }
        Apply(ErrorRx(), Red);
        Apply(WarnRx(), Yellow);
        Apply(OkRx(), Green);
        Apply(AddressRx(), Cyan);
        return result;
    }
}
