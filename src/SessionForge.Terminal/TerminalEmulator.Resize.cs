namespace SessionForge.Terminal;

public sealed partial class TerminalEmulator
{
    /// <summary>
    /// Resizes the terminal. The main screen is reflowed: soft-wrapped logical lines are re-wrapped to the new width
    /// and the cursor follows its logical position. The alternate screen is cropped/extended (full-screen apps redraw).
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        lock (SyncRoot)
        {
            if (columns == Columns && rows == Rows) return;
            bool wasAlt = IsAlternateScreen;
            int altX = _x, altY = _y;

            // Reflow the main screen using the main-screen cursor (the saved cursor while the alt screen is active).
            int mx = wasAlt ? (_savedMain.Valid ? _savedMain.X : 0) : _x;
            int my = wasAlt ? (_savedMain.Valid ? _savedMain.Y : 0) : _y;
            ReflowMain(columns, rows, ref mx, ref my);

            var newAlt = new TerminalLine[rows];
            for (int y = 0; y < rows; y++)
            {
                if (y < _alt.Length) { newAlt[y] = _alt[y]; newAlt[y].Resize(columns, default); newAlt[y].IsWrapped = false; }
                else newAlt[y] = new TerminalLine(columns, default);
            }
            _alt = newAlt;

            var oldTabs = _tabStops;
            _tabStops = NewTabStops(columns);
            Array.Copy(oldTabs, _tabStops, Math.Min(oldTabs.Length, columns));

            Columns = columns;
            Rows = rows;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _pendingWrap = false;

            if (wasAlt)
            {
                _screen = _alt;
                _x = Math.Min(altX, columns - 1);
                _y = Math.Min(altY, rows - 1);
                if (_savedMain.Valid) { _savedMain.X = mx; _savedMain.Y = my; }
            }
            else
            {
                _screen = _main;
                _x = mx;
                _y = my;
            }
            _savedAlt.X = Math.Min(_savedAlt.X, columns - 1);
            _savedAlt.Y = Math.Min(_savedAlt.Y, rows - 1);
            foreach (var l in _screen) l.Touch();
            ContentVersion++;
        }
        Updated?.Invoke();
    }

    private void ReflowMain(int newCols, int newRows, ref int curX, ref int curY)
    {
        // 1. Physical lines: all history, then screen rows up to the last row with content (or the cursor row).
        int lastScreenRow = curY;
        for (int y = Rows - 1; y > curY; y--)
        {
            if (_main[y].ContentLength() > 0) { lastScreenRow = y; break; }
        }
        var physical = new List<TerminalLine>(_scrollback.Count + lastScreenRow + 1);
        physical.AddRange(_scrollback.Enumerate());
        int cursorPhysical = physical.Count + curY;
        for (int y = 0; y <= lastScreenRow; y++) physical.Add(_main[y]);

        var result = new List<TerminalLine>(physical.Count + 8);
        int cursorLine, cursorCol;

        if (Columns == newCols)
        {
            result.AddRange(physical);
            cursorLine = cursorPhysical;
            cursorCol = Math.Min(curX, newCols - 1);
        }
        else
        {
            cursorLine = -1;
            cursorCol = 0;
            var cells = new List<Cell>(Columns * 2);
            var comb = new Dictionary<int, string>();
            var chunks = new List<(TerminalLine Line, int Start)>();
            int i = 0;
            while (i < physical.Count)
            {
                // Gather one logical line (physical rows joined by soft wraps).
                cells.Clear();
                comb.Clear();
                int cursorOffset = -1;
                while (true)
                {
                    var line = physical[i];
                    int len = line.IsWrapped ? line.Length : line.ContentLength();
                    if (i == cursorPhysical) cursorOffset = cells.Count + curX;
                    for (int c = 0; c < len; c++)
                    {
                        if (line.GetCombining(c) is { } g) comb[cells.Count] = g;
                        cells.Add(line.Cells[c]);
                    }
                    bool wrapped = line.IsWrapped;
                    i++;
                    if (!wrapped || i >= physical.Count) break;
                }

                // Re-wrap into rows of newCols (always at least one row).
                chunks.Clear();
                int pos = 0;
                do
                {
                    var nl = new TerminalLine(newCols, default);
                    int start = pos, col = 0;
                    while (pos < cells.Count && col < newCols)
                    {
                        var cell = cells[pos];
                        if (cell.Width == 0 && col == 0) { pos++; continue; }          // orphaned wide trailer
                        if (cell.Width == 2 && col == newCols - 1) break;                // wide char doesn't fit: next row
                        nl.Set(col, cell.Rune, cell.Width, cell.Attributes);
                        if (comb.TryGetValue(pos, out var g)) foreach (var r in g.EnumerateRunes()) nl.AppendCombining(col, r.Value);
                        col++;
                        pos++;
                    }
                    chunks.Add((nl, start));
                    nl.IsWrapped = pos < cells.Count;
                } while (pos < cells.Count);

                if (cursorOffset >= 0)
                {
                    int k = chunks.Count - 1;
                    while (k > 0 && chunks[k].Start > cursorOffset) k--;
                    int rel = cursorOffset - chunks[k].Start;
                    while (rel >= newCols)
                    {
                        // Cursor lies past the content (e.g. after typed spaces): extend with blank rows.
                        chunks[^1].Line.IsWrapped = true;
                        chunks.Add((new TerminalLine(newCols, default), chunks[^1].Start + newCols));
                        k++;
                        rel -= newCols;
                    }
                    cursorLine = result.Count + k;
                    cursorCol = rel;
                }
                foreach (var c in chunks) result.Add(c.Line);
            }
            if (cursorLine < 0) { cursorLine = Math.Max(0, result.Count - 1); cursorCol = 0; }
        }

        // 2. Visible window = last newRows lines, but the cursor must stay on screen.
        int screenTop = Math.Max(0, result.Count - newRows);
        if (cursorLine < screenTop) screenTop = cursorLine;

        var screen = new TerminalLine[newRows];
        for (int y = 0; y < newRows; y++)
        {
            int idx = screenTop + y;
            if (idx < result.Count) { screen[y] = result[idx]; screen[y].Resize(newCols, default); }
            else screen[y] = new TerminalLine(newCols, default);
        }

        // 3. Lines above the window become history.
        var history = result.GetRange(0, screenTop);
        foreach (var h in history) h.Resize(newCols, default);
        _scrollback.Rebuild(history);

        _main = screen;
        curY = Math.Clamp(cursorLine - screenTop, 0, newRows - 1);
        curX = Math.Clamp(cursorCol, 0, newCols - 1);
    }
}
