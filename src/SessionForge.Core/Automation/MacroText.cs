using System.Globalization;
using System.Text;

namespace SessionForge.Core.Automation;

/// <summary>Escape handling for macro text: \r \n \t \e \a \b \\ \xHH \uHHHH and ^X control notation (^C, ^[ ...).</summary>
public static class MacroText
{
    public static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length && s[i + 1] is >= '@' and <= '_' or >= 'a' and <= 'z' or '?')
            {
                char n = char.ToUpperInvariant(s[++i]);
                sb.Append(n == '?' ? '\x7f' : (char)(n - '@'));
                continue;
            }
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            char e = s[++i];
            switch (e)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'e': sb.Append('\x1b'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case '^': sb.Append('^'); break;
                case '\\': sb.Append('\\'); break;
                case 'x' when i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]):
                    sb.Append((char)int.Parse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                case 'u' when i + 4 < s.Length && s.AsSpan(i + 1, 4).ToString().All(IsHex):
                    sb.Append((char)int.Parse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: sb.Append('\\').Append(e); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Escapes control characters for display/editing (inverse of <see cref="Unescape"/> for common cases).</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\x1b': sb.Append("\\e"); break;
                case '\\': sb.Append("\\\\"); break;
                case '^': sb.Append("\\^"); break;
                case < ' ' or '\x7f': sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
