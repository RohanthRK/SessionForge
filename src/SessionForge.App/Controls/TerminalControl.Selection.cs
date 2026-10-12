using System.Windows;
using SessionForge.Terminal;

namespace SessionForge.App.Controls;

public sealed partial class TerminalControl
{
    // Selection endpoints are stored as absolute line ids (FirstLineId + index) so they survive scrollback eviction.
    private (long Line, int Col)? _selAnchor, _selActive;
    private bool _selBlock;
    private BufferRange? _searchMatch;

    public bool HasSelection => _selAnchor != null && _selActive != null && _selAnchor != _selActive;

    public void ClearSelection()
    {
        if (_selAnchor == null && _selActive == null) return;
        _selAnchor = _selActive = null;
        InvalidateTerminal();
    }

    public void SelectAll()
    {
        if (_emulator == null) return;
        lock (_emulator.SyncRoot)
        {
            _selAnchor = (_emulator.FirstLineId, 0);
            _selActive = (_emulator.FirstLineId + _emulator.TotalLines - 1, _emulator.Columns - 1);
        }
        _selBlock = false;
        InvalidateTerminal();
    }

    private void SetSelection(BufferPoint anchor, BufferPoint active, bool block)
    {
        if (_emulator == null) return;
        long first = _emulator.FirstLineId;
        _selAnchor = (first + anchor.Line, anchor.Column);
        _selActive = (first + active.Line, active.Column);
        _selBlock = block;
        InvalidateTerminal();
    }

    private void ExtendSelection(BufferPoint active)
    {
        if (_emulator == null || _selAnchor == null) return;
        _selActive = (_emulator.FirstLineId + active.Line, active.Column);
        InvalidateTerminal();
    }

    /// <summary>Selection as an ordered (start, end) in current line indices, or null.</summary>
    private (BufferPoint Start, BufferPoint End, bool Block)? NormalizedSelection(TerminalEmulator emu)
    {
        if (_selAnchor is not { } a || _selActive is not { } b || a == b) return null;
        long first = emu.FirstLineId;
        var pa = new BufferPoint((int)Math.Clamp(a.Line - first, -1, int.MaxValue), a.Col);
        var pb = new BufferPoint((int)Math.Clamp(b.Line - first, -1, int.MaxValue), b.Col);
        if (pb < pa) (pa, pb) = (pb, pa);
        if (pa.Line < 0) pa = new BufferPoint(0, 0);
        if (pb.Line < 0) return null;
        return (pa, pb, _selBlock);
    }

    private static (int, int) SelectionColumns((BufferPoint Start, BufferPoint End, bool Block)? sel, int line, int columns)
    {
        if (sel is not { } s || line < s.Start.Line || line > s.End.Line) return (-1, -1);
        if (s.Block)
        {
            int c0 = Math.Min(s.Start.Column, s.End.Column), c1 = Math.Max(s.Start.Column, s.End.Column);
            return (c0, c1);
        }
        int start = line == s.Start.Line ? s.Start.Column : 0;
        int end = line == s.End.Line ? s.End.Column : columns - 1;
        return (start, end);
    }

    private static (int, int) MatchColumns(BufferRange? m, int line)
    {
        if (m is not { } r || line < r.Start.Line || line > r.End.Line) return (-1, -1);
        int s = line == r.Start.Line ? r.Start.Column : 0;
        int e = line == r.End.Line ? r.End.Column : int.MaxValue;
        return (s, e);
    }

    public string GetSelectedText()
    {
        if (_emulator == null) return string.Empty;
        lock (_emulator.SyncRoot)
        {
            var sel = NormalizedSelection(_emulator);
            if (sel is not { } s) return string.Empty;
            return _emulator.GetText(s.Start, s.End, s.Block);
        }
    }

    public bool CopySelection()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return false;
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; } // clipboard busy
    }

    #region Search

    public BufferRange? CurrentMatch => _searchMatch;

    /// <summary>Finds the next/previous match from the current match (or viewport) and scrolls to it.</summary>
    public bool FindNext(string query, SearchOptions options)
    {
        if (_emulator == null || string.IsNullOrEmpty(query)) { ClearSearch(); return false; }
        BufferPoint from;
        if (_searchMatch is { } cur) from = options.Backward ? cur.Start : cur.Start;
        else from = options.Backward ? new BufferPoint(_viewportTop + Rows, 0) : new BufferPoint(_viewportTop - 1, int.MaxValue);
        BufferRange? r;
        try { r = _emulator.Find(query, from, options); }
        catch (ArgumentException) { return false; } // invalid regex
        if (r == null)
        {
            // wrap around
            var wrapFrom = options.Backward ? new BufferPoint(_emulator.TotalLines, 0) : new BufferPoint(-1, 0);
            try { r = _emulator.Find(query, wrapFrom, options); } catch (ArgumentException) { return false; }
        }
        _searchMatch = r;
        if (r is { } m)
        {
            _follow = false;
            Reveal(m.Start.Line);
        }
        InvalidateTerminal();
        return r != null;
    }

    public void ClearSearch()
    {
        _searchMatch = null;
        InvalidateTerminal();
    }

    #endregion

    /// <summary>URL under a point: OSC 8 hyperlink first, otherwise a plain-text URL detected on the logical line.</summary>
    public string? LinkAt(Point p)
    {
        if (_emulator == null) return null;
        var bp = HitTestCell(p);
        lock (_emulator.SyncRoot)
        {
            if (bp.Line < 0 || bp.Line >= _emulator.TotalLines) return null;
            var line = _emulator.GetLine(bp.Line);
            if (bp.Column < line.Length && _emulator.GetHyperlink(line[bp.Column].Attributes.LinkId) is { } uri) return uri;
            var range = _emulator.GetLogicalLineAt(bp.Line);
            var text = _emulator.GetText(range.Start, range.End);
            int offset = (bp.Line - range.Start.Line) * _emulator.Columns + bp.Column;
            foreach (System.Text.RegularExpressions.Match m in UrlRegex.Matches(text))
                if (offset >= m.Index && offset < m.Index + m.Length) return m.Value.TrimEnd('.', ',', ')', ']', '\'', '"', ';', ':');
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex UrlRegex =
        new(@"\b(?:https?|ftp|sftp|ssh)://[^\s<>""'`]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
