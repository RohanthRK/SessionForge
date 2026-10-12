namespace SessionForge.Terminal;

public sealed partial class TerminalEmulator
{
    public void CsiDispatch(CsiParams p, char priv, string inter, char final)
    {
        if (priv == '\0' && inter.Length == 0) { CsiPlain(p, final); return; }
        if (priv == '?' && inter.Length == 0)
        {
            switch (final)
            {
                case 'h': for (int i = 0; i < Math.Max(1, p.Count); i++) SetDecMode(p[i], true); _modesDirty = true; return;
                case 'l': for (int i = 0; i < Math.Max(1, p.Count); i++) SetDecMode(p[i], false); _modesDirty = true; return;
                case 'n': DeviceStatusPrivate(p.Get(0, 0)); return;
                case 'J': EraseDisplay(p[0]); return; // DECSED (selective erase treated as normal)
                case 'K': EraseLine(p[0]); return;
                case 'u': return; // kitty keyboard query: unsupported, no reply (apps then fall back)
            }
            return;
        }
        if (priv == '>' && final == 'c' && inter.Length == 0) { Respond("\x1b[>0;10;1c"); return; }
        if (priv == '>' && final == 'q' && inter.Length == 0) { Respond($"\x1bP>|{ProductName}\x1b\\"); return; }
        if (priv == '=' && final == 'c') { Respond("\x1bP!|00000000\x1b\\"); return; }
        if (priv == '\0' && inter == " " && final == 'q') { SetCursorStyle(p[0]); return; }
        if (priv == '\0' && inter == "!" && final == 'p') { SoftReset(); return; }
        if (inter == "$" && final == 'p') { ReportMode(p[0], priv == '?'); return; }
        if (priv == '\0' && inter == "\"" && final == 'q') return; // DECSCA
    }

    private void CsiPlain(CsiParams p, char final)
    {
        switch (final)
        {
            case '@': _screen[_y].InsertCells(_x, p.Get(0, 1), EraseAttr); _pendingWrap = false; break;
            case 'A': CursorUp(p.Get(0, 1)); break;
            case 'B': case 'e': CursorDown(p.Get(0, 1)); break;
            case 'C': case 'a': _x = Math.Min(Columns - 1, _x + p.Get(0, 1)); _pendingWrap = false; break;
            case 'D': _x = Math.Max(0, _x - p.Get(0, 1)); _pendingWrap = false; break;
            case 'E': CursorDown(p.Get(0, 1)); _x = 0; break;
            case 'F': CursorUp(p.Get(0, 1)); _x = 0; break;
            case 'G': case '`': _x = Math.Clamp(p.Get(0, 1) - 1, 0, Columns - 1); _pendingWrap = false; break;
            case 'H': case 'f': SetCursor(p.Get(1, 1) - 1, p.Get(0, 1) - 1); break;
            case 'I': TabForward(p.Get(0, 1)); break;
            case 'J': EraseDisplay(p[0]); break;
            case 'K': EraseLine(p[0]); break;
            case 'L': InsertLines(p.Get(0, 1)); break;
            case 'M': DeleteLines(p.Get(0, 1)); break;
            case 'P': _screen[_y].DeleteCells(_x, p.Get(0, 1), EraseAttr); _pendingWrap = false; break;
            case 'S': ScrollUp(p.Get(0, 1), _scrollTop, _scrollBottom); break;
            case 'T': if (p.Count <= 1) ScrollDown(p.Get(0, 1), _scrollTop, _scrollBottom); break;
            case 'X': EraseCells(_screen[_y], _x, _x + p.Get(0, 1)); _pendingWrap = false; break;
            case 'Z': TabBackward(p.Get(0, 1)); break;
            case 'b':
                {
                    int n = Math.Min(p.Get(0, 1), Columns * Rows);
                    for (int i = 0; i < n; i++) Print(_lastPrinted);
                    break;
                }
            case 'c': if (p[0] == 0) Respond("\x1b[?62;22c"); break;
            case 'd': SetCursor(_x, p.Get(0, 1) - 1); break;
            case 'g':
                if (p[0] == 0) _tabStops[_x] = false;
                else if (p[0] == 3) Array.Clear(_tabStops);
                break;
            case 'h': for (int i = 0; i < Math.Max(1, p.Count); i++) SetAnsiMode(p[i], true); break;
            case 'l': for (int i = 0; i < Math.Max(1, p.Count); i++) SetAnsiMode(p[i], false); break;
            case 'm': Sgr(p); break;
            case 'n':
                if (p[0] == 5) Respond("\x1b[0n");
                else if (p[0] == 6) Respond($"\x1b[{(Modes.OriginMode ? _y - _scrollTop : _y) + 1};{_x + 1}R");
                break;
            case 'r':
                {
                    int top = p.Get(0, 1) - 1, bottom = p.Get(1, Rows) - 1;
                    bottom = Math.Min(bottom, Rows - 1);
                    if (top < bottom)
                    {
                        _scrollTop = top; _scrollBottom = bottom;
                        SetCursor(0, 0);
                    }
                    break;
                }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': WindowOps(p); break;
        }
    }

    private void CursorUp(int n)
    {
        int min = _y >= _scrollTop ? _scrollTop : 0;
        _y = Math.Max(min, _y - n);
        _pendingWrap = false;
    }

    private void CursorDown(int n)
    {
        int max = _y <= _scrollBottom ? _scrollBottom : Rows - 1;
        _y = Math.Min(max, _y + n);
        _pendingWrap = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_screen[_y], _x, Columns);
                _screen[_y].IsWrapped = false;
                for (int y = _y + 1; y < Rows; y++) { EraseCells(_screen[y], 0, Columns); _screen[y].IsWrapped = false; }
                break;
            case 1:
                for (int y = 0; y < _y; y++) { EraseCells(_screen[y], 0, Columns); _screen[y].IsWrapped = false; }
                EraseCells(_screen[_y], 0, _x + 1);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) { EraseCells(_screen[y], 0, Columns); _screen[y].IsWrapped = false; }
                break;
            case 3:
                if (_screen == _main) _scrollback.Clear();
                break;
        }
        _pendingWrap = false;
    }

    private void EraseLine(int mode)
    {
        var line = _screen[_y];
        switch (mode)
        {
            case 0: EraseCells(line, _x, Columns); line.IsWrapped = false; break;
            case 1: EraseCells(line, 0, _x + 1); break;
            case 2: EraseCells(line, 0, Columns); line.IsWrapped = false; break;
        }
        _pendingWrap = false;
    }

    private void InsertLines(int n)
    {
        if (_y < _scrollTop || _y > _scrollBottom) return;
        ScrollDown(n, _y, _scrollBottom);
        _x = 0; _pendingWrap = false;
    }

    private void DeleteLines(int n)
    {
        if (_y < _scrollTop || _y > _scrollBottom) return;
        // Deleting lines inside the region never pushes to scrollback.
        n = Math.Min(n, _scrollBottom - _y + 1);
        var ea = EraseAttr;
        for (int k = 0; k < n; k++)
        {
            var first = _screen[_y];
            Array.Copy(_screen, _y + 1, _screen, _y, _scrollBottom - _y);
            first.Reset(Columns, ea);
            _screen[_scrollBottom] = first;
        }
        for (int y = _y; y <= _scrollBottom; y++) _screen[y].Touch();
        _x = 0; _pendingWrap = false;
    }

    private void SetCursorStyle(int ps)
    {
        (Modes.CursorShape, Modes.CursorBlink) = ps switch
        {
            0 or 1 => (CursorShape.Block, true),
            2 => (CursorShape.Block, false),
            3 => (CursorShape.Underline, true),
            4 => (CursorShape.Underline, false),
            5 => (CursorShape.Bar, true),
            6 => (CursorShape.Bar, false),
            _ => (Modes.CursorShape, Modes.CursorBlink),
        };
        _modesDirty = true;
    }

    private void SetAnsiMode(int mode, bool on)
    {
        switch (mode)
        {
            case 4: Modes.InsertMode = on; break;
            case 20: Modes.LineFeedNewLine = on; break;
        }
    }

    private void SetDecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: Modes.ApplicationCursorKeys = on; break;
            case 5: Modes.ReverseVideo = on; break;
            case 6: Modes.OriginMode = on; SetCursor(0, 0); break;
            case 7: Modes.AutoWrap = on; if (!on) _pendingWrap = false; break;
            case 9: Modes.MouseTracking = on ? MouseTrackingMode.X10 : MouseTrackingMode.None; break;
            case 12: Modes.CursorBlink = on; break;
            case 25: Modes.CursorVisible = on; break;
            case 47: case 1047: SwitchScreen(on, clearAlt: mode == 1047 && on); break;
            case 1049:
                if (on) { SaveCursor(); SwitchScreen(true, clearAlt: true); }
                else { SwitchScreen(false, false); RestoreCursor(); }
                break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 66: Modes.ApplicationKeypad = on; break;
            case 1000: Modes.MouseTracking = on ? MouseTrackingMode.Normal : MouseTrackingMode.None; break;
            case 1002: Modes.MouseTracking = on ? MouseTrackingMode.ButtonEvent : MouseTrackingMode.None; break;
            case 1003: Modes.MouseTracking = on ? MouseTrackingMode.AnyEvent : MouseTrackingMode.None; break;
            case 1004: Modes.FocusEvents = on; break;
            case 1005: Modes.MouseEncoding = on ? MouseEncoding.Utf8 : MouseEncoding.Default; break;
            case 1006: Modes.MouseEncoding = on ? MouseEncoding.Sgr : MouseEncoding.Default; break;
            case 1015: Modes.MouseEncoding = on ? MouseEncoding.Urxvt : MouseEncoding.Default; break;
            case 1007: Modes.AlternateScroll = on; break;
            case 2004: Modes.BracketedPaste = on; break;
            case 2026: Modes.SynchronizedOutput = on; break;
        }
    }

    private void SwitchScreen(bool alt, bool clearAlt)
    {
        if (alt == IsAlternateScreen) { if (alt && clearAlt) foreach (var l in _alt) l.Reset(Columns, EraseAttr); return; }
        if (alt)
        {
            _screen = _alt;
            if (clearAlt) foreach (var l in _alt) l.Reset(Columns, EraseAttr);
        }
        else _screen = _main;
        foreach (var l in _screen) l.Touch();
        _pendingWrap = false;
    }

    private void ReportMode(int mode, bool dec)
    {
        // 0 = not recognised, 1 = set, 2 = reset
        int state;
        if (dec)
        {
            state = mode switch
            {
                1 => Modes.ApplicationCursorKeys ? 1 : 2,
                5 => Modes.ReverseVideo ? 1 : 2,
                6 => Modes.OriginMode ? 1 : 2,
                7 => Modes.AutoWrap ? 1 : 2,
                12 => Modes.CursorBlink ? 1 : 2,
                25 => Modes.CursorVisible ? 1 : 2,
                47 or 1047 or 1049 => IsAlternateScreen ? 1 : 2,
                1000 => Modes.MouseTracking == MouseTrackingMode.Normal ? 1 : 2,
                1002 => Modes.MouseTracking == MouseTrackingMode.ButtonEvent ? 1 : 2,
                1003 => Modes.MouseTracking == MouseTrackingMode.AnyEvent ? 1 : 2,
                1004 => Modes.FocusEvents ? 1 : 2,
                1006 => Modes.MouseEncoding == MouseEncoding.Sgr ? 1 : 2,
                2004 => Modes.BracketedPaste ? 1 : 2,
                2026 => Modes.SynchronizedOutput ? 1 : 2,
                _ => 0,
            };
            Respond($"\x1b[?{mode};{state}$y");
        }
        else
        {
            state = mode switch { 4 => Modes.InsertMode ? 1 : 2, 20 => Modes.LineFeedNewLine ? 1 : 2, _ => 0 };
            Respond($"\x1b[{mode};{state}$y");
        }
    }

    private void DeviceStatusPrivate(int ps)
    {
        if (ps == 6) Respond($"\x1b[?{(Modes.OriginMode ? _y - _scrollTop : _y) + 1};{_x + 1};1R");
    }

    private void WindowOps(CsiParams p)
    {
        switch (p[0])
        {
            case 18: Respond($"\x1b[8;{Rows};{Columns}t"); break;
            case 22: if (_titleStack.Count < 16) _titleStack.Push(Title); break;
            case 23: if (_titleStack.TryPop(out var t)) { Title = t; _titleDirty = true; } break;
        }
    }

    #region SGR

    private void Sgr(CsiParams p)
    {
        if (p.Count == 0) { _attr = default; return; }
        var fg = _attr.Foreground;
        var bg = _attr.Background;
        var f = _attr.Flags;
        for (int i = 0; i < p.Count; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: fg = default; bg = default; f = CellFlags.None; break;
                case 1: f |= CellFlags.Bold; break;
                case 2: f |= CellFlags.Dim; break;
                case 3: f |= CellFlags.Italic; break;
                case 4:
                    f &= ~(CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.CurlyUnderline);
                    if (p.IsSub(i + 1))
                    {
                        int style = p[++i];
                        f |= style switch { 0 => CellFlags.None, 2 => CellFlags.DoubleUnderline, 3 => CellFlags.CurlyUnderline, _ => CellFlags.Underline };
                        while (p.IsSub(i + 1)) i++;
                    }
                    else f |= CellFlags.Underline;
                    break;
                case 5: case 6: f |= CellFlags.Blink; break;
                case 7: f |= CellFlags.Inverse; break;
                case 8: f |= CellFlags.Hidden; break;
                case 9: f |= CellFlags.Strikethrough; break;
                case 21: f |= CellFlags.DoubleUnderline; break;
                case 22: f &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: f &= ~CellFlags.Italic; break;
                case 24: f &= ~(CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.CurlyUnderline); break;
                case 25: f &= ~CellFlags.Blink; break;
                case 27: f &= ~CellFlags.Inverse; break;
                case 28: f &= ~CellFlags.Hidden; break;
                case 29: f &= ~CellFlags.Strikethrough; break;
                case >= 30 and <= 37: fg = TerminalColor.Indexed(v - 30); break;
                case 38: fg = ParseExtendedColor(p, ref i, fg); break;
                case 39: fg = default; break;
                case >= 40 and <= 47: bg = TerminalColor.Indexed(v - 40); break;
                case 48: bg = ParseExtendedColor(p, ref i, bg); break;
                case 49: bg = default; break;
                case 53: f |= CellFlags.Overline; break;
                case 55: f &= ~CellFlags.Overline; break;
                case 58: ParseExtendedColor(p, ref i, default); break; // underline colour: parsed, not rendered separately
                case 59: break;
                case >= 90 and <= 97: fg = TerminalColor.Indexed(v - 90 + 8); break;
                case >= 100 and <= 107: bg = TerminalColor.Indexed(v - 100 + 8); break;
            }
        }
        _attr = new CellAttributes(fg, bg, f, _attr.LinkId);
    }

    /// <summary>Parses 38/48 extended colour in both ';' and ':' forms. <paramref name="i"/> points at the 38/48.</summary>
    private static TerminalColor ParseExtendedColor(CsiParams p, ref int i, TerminalColor current)
    {
        bool colon = p.IsSub(i + 1);
        if (i + 1 >= p.Count) return current;
        int kind = p[i + 1];
        if (kind == 5)
        {
            if (i + 2 >= p.Count) { i = p.Count; return current; }
            int idx = p[i + 2];
            i += 2;
            return TerminalColor.Indexed(Math.Clamp(idx, 0, 255));
        }
        if (kind == 2)
        {
            int start = i + 2;
            // Colon form may include a colour-space id: 38:2:<cs>:r:g:b
            if (colon)
            {
                int subs = 0;
                while (p.IsSub(start + subs)) subs++;
                if (subs >= 4) start++;
                int r = p[start], g = p[start + 1], b = p[start + 2];
                i = start + 2;
                while (p.IsSub(i + 1)) i++;
                return TerminalColor.Rgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
            }
            if (start + 2 >= p.Count) { i = p.Count; return current; }
            var c = TerminalColor.Rgb((byte)Math.Clamp(p[start], 0, 255), (byte)Math.Clamp(p[start + 1], 0, 255), (byte)Math.Clamp(p[start + 2], 0, 255));
            i = start + 2;
            return c;
        }
        i++;
        return current;
    }

    #endregion
}
