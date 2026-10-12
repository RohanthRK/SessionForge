using System.Text;
using System.Text.RegularExpressions;

namespace SessionForge.Terminal;

/// <summary>A position in the combined scrollback+screen buffer (line index as in <see cref="TerminalEmulator.GetLine"/>).</summary>
public readonly record struct BufferPoint(int Line, int Column) : IComparable<BufferPoint>
{
    public int CompareTo(BufferPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
    public static bool operator <(BufferPoint a, BufferPoint b) => a.CompareTo(b) < 0;
    public static bool operator >(BufferPoint a, BufferPoint b) => a.CompareTo(b) > 0;
    public static bool operator <=(BufferPoint a, BufferPoint b) => a.CompareTo(b) <= 0;
    public static bool operator >=(BufferPoint a, BufferPoint b) => a.CompareTo(b) >= 0;
}

/// <summary>Inclusive range of cells.</summary>
public readonly record struct BufferRange(BufferPoint Start, BufferPoint End);

public sealed record SearchOptions(bool MatchCase = false, bool Regex = false, bool Backward = false);

public sealed partial class TerminalEmulator
{
    /// <summary>Text between two points (inclusive). Soft-wrapped lines are joined without a newline.</summary>
    public string GetText(BufferPoint a, BufferPoint b, bool block = false)
    {
        lock (SyncRoot)
        {
            if (b < a) (a, b) = (b, a);
            int last = TotalLines - 1;
            a = a with { Line = Math.Clamp(a.Line, 0, last) };
            b = b with { Line = Math.Clamp(b.Line, 0, last) };
            var sb = new StringBuilder();
            if (block)
            {
                int c0 = Math.Min(a.Column, b.Column), c1 = Math.Max(a.Column, b.Column);
                for (int l = a.Line; l <= b.Line; l++)
                {
                    sb.Append(GetLine(l).GetText(c0, c1 + 1));
                    if (l < b.Line) sb.Append("\r\n");
                }
                return sb.ToString();
            }
            for (int l = a.Line; l <= b.Line; l++)
            {
                var line = GetLine(l);
                int s = l == a.Line ? a.Column : 0;
                int e = l == b.Line ? b.Column + 1 : line.Length;
                bool wrapped = line.IsWrapped && l < b.Line;
                sb.Append(line.GetText(s, e, trimEnd: !wrapped));
                if (l < b.Line && !wrapped) sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>Whole buffer as text (history + screen), e.g. for "save terminal output".</summary>
    public string GetAllText()
    {
        lock (SyncRoot)
        {
            int last = TotalLines - 1;
            while (last > 0 && GetLine(last).ContentLength() == 0) last--;
            return GetText(new BufferPoint(0, 0), new BufferPoint(last, Columns - 1));
        }
    }

    public const string DefaultWordDelimiters = " \t\"'`()[]{}<>|;,&";

    /// <summary>Range of the word under a point (double-click selection), following soft wraps.</summary>
    public BufferRange GetWordAt(BufferPoint p, string delimiters = DefaultWordDelimiters)
    {
        lock (SyncRoot)
        {
            var line = GetLine(Math.Clamp(p.Line, 0, TotalLines - 1));
            int col = Math.Clamp(p.Column, 0, line.Length - 1);
            if (line[col].Width == 0 && col > 0) col--;
            bool IsWord(int l, int c)
            {
                ref readonly var cell = ref GetLine(l)[c];
                if (cell.Width == 0) return true;
                return cell.Rune != 0 && delimiters.IndexOf((char)Math.Min(cell.Rune, 0xFFFF)) < 0;
            }
            if (!IsWord(p.Line, col)) return new BufferRange(p with { Column = col }, p with { Column = col });

            var start = new BufferPoint(p.Line, col);
            while (true)
            {
                if (start.Column > 0) { if (!IsWord(start.Line, start.Column - 1)) break; start = start with { Column = start.Column - 1 }; }
                else if (start.Line > 0 && GetLine(start.Line - 1).IsWrapped && IsWord(start.Line - 1, Columns - 1)) start = new BufferPoint(start.Line - 1, Columns - 1);
                else break;
            }
            var end = new BufferPoint(p.Line, col);
            while (true)
            {
                if (end.Column < Columns - 1) { if (!IsWord(end.Line, end.Column + 1)) break; end = end with { Column = end.Column + 1 }; }
                else if (GetLine(end.Line).IsWrapped && end.Line + 1 < TotalLines && IsWord(end.Line + 1, 0)) end = new BufferPoint(end.Line + 1, 0);
                else break;
            }
            if (GetLine(end.Line)[end.Column].Width == 2) end = end with { Column = Math.Min(Columns - 1, end.Column + 1) };
            return new BufferRange(start, end);
        }
    }

    /// <summary>Range covering the logical line (following soft wraps) at a point; used by triple-click.</summary>
    public BufferRange GetLogicalLineAt(int lineIndex)
    {
        lock (SyncRoot)
        {
            int s = Math.Clamp(lineIndex, 0, TotalLines - 1), e = s;
            while (s > 0 && GetLine(s - 1).IsWrapped) s--;
            while (e < TotalLines - 1 && GetLine(e).IsWrapped) e++;
            return new BufferRange(new BufferPoint(s, 0), new BufferPoint(e, Columns - 1));
        }
    }

    /// <summary>
    /// Finds the next match strictly after (or before, when searching backward) <paramref name="from"/>.
    /// Matches can span soft-wrapped rows. Returns null if not found. Invalid regex throws <see cref="ArgumentException"/>.
    /// </summary>
    public BufferRange? Find(string query, BufferPoint from, SearchOptions options)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var regex = new Regex(options.Regex ? query : Regex.Escape(query),
            (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        lock (SyncRoot)
        {
            int total = TotalLines;
            // Build logical lines lazily while scanning.
            if (!options.Backward)
            {
                int l = Math.Clamp(from.Line, 0, total - 1);
                while (l > 0 && GetLine(l - 1).IsWrapped) l--;
                while (l < total)
                {
                    var (text, map, next) = BuildLogical(l);
                    foreach (Match m in regex.Matches(text))
                    {
                        if (m.Length == 0) continue;
                        var s = map[m.Index];
                        if (s > from) return new BufferRange(s, map[m.Index + m.Length - 1]);
                    }
                    l = next;
                }
            }
            else
            {
                int l = Math.Clamp(from.Line, 0, total - 1);
                while (l < total - 1 && GetLine(l).IsWrapped) l++;
                while (l >= 0)
                {
                    int startLine = l;
                    while (startLine > 0 && GetLine(startLine - 1).IsWrapped) startLine--;
                    var (text, map, _) = BuildLogical(startLine);
                    var matches = regex.Matches(text);
                    for (int k = matches.Count - 1; k >= 0; k--)
                    {
                        var m = matches[k];
                        if (m.Length == 0) continue;
                        var s = map[m.Index];
                        if (s < from) return new BufferRange(s, map[m.Index + m.Length - 1]);
                    }
                    l = startLine - 1;
                }
            }
            return null;
        }
    }

    /// <summary>Counts all matches (for "n matches" UI). Capped for responsiveness.</summary>
    public int CountMatches(string query, SearchOptions options, int cap = 9999)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        Regex regex;
        try
        {
            regex = new Regex(options.Regex ? query : Regex.Escape(query),
                (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException) { return 0; }
        lock (SyncRoot)
        {
            int n = 0, l = 0;
            while (l < TotalLines && n < cap)
            {
                var (text, _, next) = BuildLogical(l);
                n += regex.Matches(text).Count(m => m.Length > 0);
                l = next;
            }
            return Math.Min(n, cap);
        }
    }

    private (string Text, BufferPoint[] Map, int Next) BuildLogical(int line)
    {
        var sb = new StringBuilder();
        var map = new List<BufferPoint>();
        int l = line;
        while (true)
        {
            var tl = GetLine(l);
            int len = tl.IsWrapped ? tl.Length : tl.ContentLength();
            for (int c = 0; c < len; c++)
            {
                ref readonly var cell = ref tl[c];
                if (cell.Width == 0) continue;
                int before = sb.Length;
                if (cell.Rune == 0) sb.Append(' '); else TerminalLine.AppendRune(sb, cell.Rune);
                if (tl.GetCombining(c) is { } g) sb.Append(g);
                for (int k = before; k < sb.Length; k++) map.Add(new BufferPoint(l, cell.Width == 2 ? c + 1 : c));
                // For wide chars the end maps to the trailer so highlights cover both cells; start fixed below.
                if (cell.Width == 2) map[before] = new BufferPoint(l, c);
            }
            l++;
            if (!tl.IsWrapped || l >= TotalLines) break;
        }
        map.Add(new BufferPoint(Math.Max(line, l - 1), 0)); // sentinel
        return (sb.ToString(), map.ToArray(), l);
    }
}
