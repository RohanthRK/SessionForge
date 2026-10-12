using System.Text;

namespace SessionForge.Terminal;

/// <summary>
/// One physical row of cells. <see cref="IsWrapped"/> means the logical line continues on the next row
/// (soft wrap), which drives copy/paste joining, search and reflow on resize.
/// </summary>
public sealed class TerminalLine
{
    private Cell[] _cells;
    private Dictionary<int, string>? _combining;

    public TerminalLine(int columns, in CellAttributes fill)
    {
        _cells = new Cell[columns];
        Fill(0, columns, fill);
    }

    public int Length => _cells.Length;
    public bool IsWrapped { get; set; }

    /// <summary>Incremented on every mutation; renderers use it to cache line visuals.</summary>
    public int Version { get; private set; }

    public ref readonly Cell this[int column] => ref _cells[column];

    public ReadOnlySpan<Cell> Cells => _cells;

    public void Touch() => Version++;

    public void Set(int column, int rune, byte width, in CellAttributes attr)
    {
        ref var c = ref _cells[column];
        c.Rune = rune;
        c.Width = width;
        c.Attributes = attr;
        _combining?.Remove(column);
        Version++;
    }

    /// <summary>Extra combining characters (accents, ZWJ sequences) attached to the cell at <paramref name="column"/>.</summary>
    public string? GetCombining(int column) => _combining != null && _combining.TryGetValue(column, out var s) ? s : null;

    public void AppendCombining(int column, int codePoint)
    {
        _combining ??= new Dictionary<int, string>();
        var add = char.ConvertFromUtf32(codePoint);
        _combining[column] = _combining.TryGetValue(column, out var s) ? s + add : add;
        Version++;
    }

    public void Fill(int start, int end, in CellAttributes attr)
    {
        end = Math.Min(end, _cells.Length);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            ref var c = ref _cells[i];
            c.Rune = 0;
            c.Width = 1;
            c.Attributes = attr;
            _combining?.Remove(i);
        }
        Version++;
    }

    public void Reset(int columns, in CellAttributes attr)
    {
        if (_cells.Length != columns) _cells = new Cell[columns];
        _combining = null;
        IsWrapped = false;
        Fill(0, columns, attr);
    }

    /// <summary>Inserts <paramref name="count"/> blank cells at <paramref name="column"/>, shifting the rest right (ICH).</summary>
    public void InsertCells(int column, int count, in CellAttributes attr)
    {
        if (column >= _cells.Length) return;
        count = Math.Min(count, _cells.Length - column);
        Array.Copy(_cells, column, _cells, column + count, _cells.Length - column - count);
        ShiftCombining(column, count);
        Fill(column, column + count, attr);
        FixWideBoundary(_cells.Length - 1, attr);
    }

    /// <summary>Deletes <paramref name="count"/> cells at <paramref name="column"/>, shifting the rest left (DCH).</summary>
    public void DeleteCells(int column, int count, in CellAttributes attr)
    {
        if (column >= _cells.Length) return;
        count = Math.Min(count, _cells.Length - column);
        Array.Copy(_cells, column + count, _cells, column, _cells.Length - column - count);
        ShiftCombining(column, -count);
        Fill(_cells.Length - count, _cells.Length, attr);
        if (column < _cells.Length && _cells[column].Width == 0) Fill(column, column + 1, attr);
    }

    private void ShiftCombining(int from, int delta)
    {
        if (_combining == null || _combining.Count == 0) return;
        var moved = new Dictionary<int, string>();
        foreach (var (k, v) in _combining)
        {
            int nk = k >= from ? k + delta : k;
            if (k >= from && delta < 0 && k < from - delta) continue; // deleted
            if (nk >= 0 && nk < _cells.Length) moved[nk] = v;
        }
        _combining = moved;
    }

    /// <summary>If a wide lead sits at the last column with its trailer cut off, blank it.</summary>
    private void FixWideBoundary(int lastCol, in CellAttributes attr)
    {
        if (lastCol >= 0 && _cells[lastCol].Width == 2) Fill(lastCol, lastCol + 1, attr);
    }

    public void Resize(int columns, in CellAttributes attr)
    {
        if (columns == _cells.Length) return;
        int old = _cells.Length;
        Array.Resize(ref _cells, columns);
        if (columns > old) Fill(old, columns, attr);
        else
        {
            if (_combining != null) foreach (var k in _combining.Keys.Where(k => k >= columns).ToList()) _combining.Remove(k);
            FixWideBoundary(columns - 1, attr);
            Version++;
        }
    }

    public void CopyFrom(TerminalLine src)
    {
        if (_cells.Length != src._cells.Length) _cells = new Cell[src._cells.Length];
        Array.Copy(src._cells, _cells, _cells.Length);
        _combining = src._combining == null ? null : new Dictionary<int, string>(src._combining);
        IsWrapped = src.IsWrapped;
        Version++;
    }

    public TerminalLine Clone()
    {
        var l = new TerminalLine(0, default);
        l.CopyFrom(this);
        return l;
    }

    /// <summary>Number of columns up to and including the last non-blank cell (or blank with a non-default background).</summary>
    public int ContentLength()
    {
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            ref readonly var c = ref _cells[i];
            if (!c.IsBlank || !c.Attributes.Background.IsDefault || c.Attributes.Has(CellFlags.Inverse)) return i + 1;
        }
        return 0;
    }

    /// <summary>Text of columns [start, end). Wide trailers are skipped; blanks become spaces.</summary>
    public string GetText(int start = 0, int end = int.MaxValue, bool trimEnd = true)
    {
        end = Math.Min(end, _cells.Length);
        var sb = new StringBuilder(Math.Max(0, end - start));
        for (int i = Math.Max(0, start); i < end; i++)
        {
            ref readonly var c = ref _cells[i];
            if (c.Width == 0) continue;
            if (c.Rune == 0) sb.Append(' ');
            else AppendRune(sb, c.Rune);
            if (_combining != null && _combining.TryGetValue(i, out var comb)) sb.Append(comb);
        }
        if (trimEnd)
        {
            int n = sb.Length;
            while (n > 0 && sb[n - 1] == ' ') n--;
            sb.Length = n;
        }
        return sb.ToString();
    }

    internal static void AppendRune(StringBuilder sb, int rune)
    {
        if (rune < 0x10000) sb.Append((char)rune);
        else sb.Append(char.ConvertFromUtf32(rune));
    }

    public override string ToString() => GetText();
}
