using System.Text;

namespace SessionForge.Terminal;

/// <summary>
/// xterm-compatible terminal emulator state machine. Feed it host output with <see cref="Feed"/>;
/// it maintains the screen, scrollback and modes, and emits replies (DSR/DA etc.) via <see cref="Response"/>.
/// Thread-safety: all mutation and reads of screen content must happen under <see cref="SyncRoot"/>.
/// Events are raised outside the lock, after <see cref="Feed"/> completes processing.
/// </summary>
public sealed partial class TerminalEmulator : IVtHandler
{
    private enum Charset : byte { Ascii, DecSpecial, Uk }

    private struct SavedCursor
    {
        public int X, Y;
        public bool PendingWrap, OriginMode, AutoWrap;
        public CellAttributes Attr;
        public Charset G0, G1, G2, G3;
        public int Gl;
        public bool Valid;
    }

    private readonly VtParser _parser;
    private readonly LineRing _scrollback;
    private TerminalLine[] _main;
    private TerminalLine[] _alt;
    private TerminalLine[] _screen;
    private bool[] _tabStops;

    private int _x, _y;
    private bool _pendingWrap;
    private CellAttributes _attr;
    private int _scrollTop, _scrollBottom;
    private readonly Charset[] _g = new Charset[4];
    private int _gl;
    private SavedCursor _savedMain, _savedAlt;
    private int _lastPrinted = ' ';
    private readonly Stack<string> _titleStack = new();

    private Encoding _encoding;
    private Decoder _decoder;
    private char[] _decodeBuf = new char[8192];

    // hyperlinks (OSC 8)
    private readonly Dictionary<int, string> _links = new();
    private readonly Dictionary<string, int> _linkIds = new(StringComparer.Ordinal);
    private int _nextLinkId = 1;

    // deferred events
    private readonly List<byte[]> _pendingResponses = new();
    private bool _titleDirty, _bellPending, _cwdDirty, _modesDirty;
    private string? _pendingClipboard;

    public TerminalEmulator(int columns = 80, int rows = 24, int scrollbackLines = 10_000)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _parser = new VtParser(this);
        _scrollback = new LineRing(scrollbackLines);
        _main = NewScreen(Columns, Rows);
        _alt = NewScreen(Columns, Rows);
        _screen = _main;
        _tabStops = NewTabStops(Columns);
        _scrollBottom = Rows - 1;
        _encoding = new UTF8Encoding(false, false);
        _decoder = _encoding.GetDecoder();
        Theme = TerminalTheme.ForgeDark();
    }

    public object SyncRoot { get; } = new();
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorX => _x;
    public int CursorY => _y;
    public bool IsAlternateScreen => _screen == _alt;
    public TerminalModes Modes { get; } = new();
    public string Title { get; private set; } = string.Empty;
    public string? WorkingDirectory { get; private set; }
    public TerminalTheme Theme { get; set; }

    /// <summary>Allow OSC 52 clipboard writes from the host. Off by default (security).</summary>
    public bool AllowClipboardWrite { get; set; }

    /// <summary>Answerback string for ENQ (^E).</summary>
    public string Answerback { get; set; } = string.Empty;

    /// <summary>Terminal reports itself in DA/XTVERSION as this name.</summary>
    public string ProductName { get; set; } = "SessionForge";

    public int ScrollbackCount => _scrollback.Count;
    public int ScrollbackCapacity { get => _scrollback.Capacity; set { lock (SyncRoot) _scrollback.SetCapacity(value); } }

    /// <summary>Absolute id of the oldest retained line. Line ids increase monotonically and survive scrollback eviction.</summary>
    public long FirstLineId => _scrollback.Evicted;

    /// <summary>Total retained lines (scrollback + screen).</summary>
    public int TotalLines => _scrollback.Count + Rows;

    /// <summary>Incremented on every content change.</summary>
    public long ContentVersion { get; private set; }

    public event Action<byte[]>? Response;
    public event Action<string>? TitleChanged;
    public event Action? Bell;
    public event Action<string>? WorkingDirectoryChanged;
    public event Action<string>? ClipboardWriteRequested;
    public event Action? ModesChanged;
    /// <summary>Raised after each Feed that changed content.</summary>
    public event Action? Updated;

    public Encoding Encoding
    {
        get => _encoding;
        set { lock (SyncRoot) { _encoding = value; _decoder = value.GetDecoder(); } }
    }

    /// <summary>Line by index across scrollback and screen: 0..ScrollbackCount-1 are history, then screen rows.</summary>
    public TerminalLine GetLine(int index) => index < _scrollback.Count ? _scrollback[index] : _screen[index - _scrollback.Count];

    /// <summary>Screen row (0-based).</summary>
    public TerminalLine GetScreenLine(int row) => _screen[row];

    public string? GetHyperlink(int id) => id != 0 && _links.TryGetValue(id, out var u) ? u : null;

    #region Feeding

    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (SyncRoot)
        {
            int max = _encoding.GetMaxCharCount(data.Length);
            if (_decodeBuf.Length < max) _decodeBuf = new char[Math.Max(max, _decodeBuf.Length * 2)];
            int n = _decoder.GetChars(data, _decodeBuf, flush: false);
            _parser.Feed(_decodeBuf.AsSpan(0, n));
            ContentVersion++;
        }
        RaiseDeferred();
    }

    public void Feed(string text)
    {
        lock (SyncRoot)
        {
            _parser.Feed(text.AsSpan());
            ContentVersion++;
        }
        RaiseDeferred();
    }

    private void RaiseDeferred()
    {
        byte[][]? responses = null;
        string? title = null, cwd = null, clip = null;
        bool bell, modes;
        lock (SyncRoot)
        {
            if (_pendingResponses.Count > 0) { responses = _pendingResponses.ToArray(); _pendingResponses.Clear(); }
            if (_titleDirty) { title = Title; _titleDirty = false; }
            if (_cwdDirty) { cwd = WorkingDirectory; _cwdDirty = false; }
            clip = _pendingClipboard; _pendingClipboard = null;
            bell = _bellPending; _bellPending = false;
            modes = _modesDirty; _modesDirty = false;
        }
        if (responses != null) foreach (var r in responses) Response?.Invoke(r);
        if (title != null) TitleChanged?.Invoke(title);
        if (cwd != null) WorkingDirectoryChanged?.Invoke(cwd);
        if (clip != null) ClipboardWriteRequested?.Invoke(clip);
        if (bell) Bell?.Invoke();
        if (modes) ModesChanged?.Invoke();
        Updated?.Invoke();
    }

    private void Respond(string s) => _pendingResponses.Add(Encoding.UTF8.GetBytes(s));

    #endregion

    #region Screen helpers

    private static TerminalLine[] NewScreen(int cols, int rows)
    {
        var s = new TerminalLine[rows];
        for (int i = 0; i < rows; i++) s[i] = new TerminalLine(cols, default);
        return s;
    }

    private static bool[] NewTabStops(int cols)
    {
        var t = new bool[cols];
        for (int i = 8; i < cols; i += 8) t[i] = true;
        return t;
    }

    private CellAttributes EraseAttr => _attr.ForErase();

    private void ScrollUp(int n, int top, int bottom)
    {
        n = Math.Min(n, bottom - top + 1);
        var ea = EraseAttr;
        for (int k = 0; k < n; k++)
        {
            var first = _screen[top];
            TerminalLine fresh;
            if (top == 0 && _screen == _main)
            {
                // The scrolled-off line moves into history; reuse whatever the ring evicted (if anything).
                fresh = _scrollback.Push(first) ?? new TerminalLine(Columns, ea);
            }
            else fresh = first;
            Array.Copy(_screen, top + 1, _screen, top, bottom - top);
            fresh.Reset(Columns, ea);
            _screen[bottom] = fresh;
        }
        foreach (var l in _screen.AsSpan(top, bottom - top + 1)) l.Touch();
    }

    private void ScrollDown(int n, int top, int bottom)
    {
        n = Math.Min(n, bottom - top + 1);
        var ea = EraseAttr;
        for (int k = 0; k < n; k++)
        {
            var last = _screen[bottom];
            Array.Copy(_screen, top, _screen, top + 1, bottom - top);
            last.Reset(Columns, ea);
            _screen[top] = last;
        }
        foreach (var l in _screen.AsSpan(top, bottom - top + 1)) l.Touch();
    }

    private void Index()
    {
        if (_y == _scrollBottom) ScrollUp(1, _scrollTop, _scrollBottom);
        else if (_y < Rows - 1) _y++;
    }

    private void ReverseIndex()
    {
        if (_y == _scrollTop) ScrollDown(1, _scrollTop, _scrollBottom);
        else if (_y > 0) _y--;
    }

    private void CarriageReturn() { _x = 0; _pendingWrap = false; }

    private void SetCursor(int x, int y)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            _y = Math.Clamp(y + _scrollTop, _scrollTop, _scrollBottom);
        }
        else _y = Math.Clamp(y, 0, Rows - 1);
        _x = Math.Clamp(x, 0, Columns - 1);
    }

    /// <summary>Erase cells in [start,end) on a row, repairing any wide character split at the edges.</summary>
    private void EraseCells(TerminalLine line, int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(Columns, end);
        if (start >= end) return;
        var ea = EraseAttr;
        if (start > 0 && line[start].Width == 0) start--;
        if (end < Columns && line[end].Width == 0) end++;
        line.Fill(start, end, ea);
    }

    #endregion

    #region IVtHandler: print / execute

    private static readonly int[] DecSpecialMap = BuildDecSpecial();

    private static int[] BuildDecSpecial()
    {
        var m = new int[128];
        for (int i = 0; i < 128; i++) m[i] = i;
        const string src = "_`abcdefghijklmnopqrstuvwxyz{|}~";
        int[] dst = [0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514,
                     0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7];
        for (int i = 0; i < src.Length; i++) m[src[i]] = dst[i];
        return m;
    }

    public void Print(int cp)
    {
        if (cp < 128)
        {
            switch (_g[_gl])
            {
                case Charset.DecSpecial: cp = DecSpecialMap[cp]; break;
                case Charset.Uk when cp == '#': cp = 0xA3; break;
            }
        }

        int width = CharWidth.Of(cp);
        if (width == 0)
        {
            AttachCombining(cp);
            return;
        }

        if (_pendingWrap && Modes.AutoWrap)
        {
            _screen[_y].IsWrapped = true;
            CarriageReturn();
            Index();
        }
        _pendingWrap = false;

        if (width == 2 && _x == Columns - 1)
        {
            if (Modes.AutoWrap)
            {
                EraseCells(_screen[_y], _x, _x + 1);
                _screen[_y].IsWrapped = true;
                CarriageReturn();
                Index();
            }
            else width = 1; // cannot fit: degrade
        }

        var line = _screen[_y];
        if (Modes.InsertMode) line.InsertCells(_x, width, EraseAttr);

        // Writing over half of an existing wide char: blank the other half.
        if (line[_x].Width == 0 && _x > 0) line.Fill(_x - 1, _x, EraseAttr);
        if (line[_x].Width == 2 && width == 1 && _x + 1 < Columns) line.Fill(_x + 1, _x + 2, EraseAttr);
        if (width == 2 && _x + 2 < Columns && line[_x + 1].Width == 2) line.Fill(_x + 2, _x + 3, EraseAttr);

        line.Set(_x, cp, (byte)width, _attr);
        if (width == 2) line.Set(_x + 1, 0, 0, _attr);
        _lastPrinted = cp;

        _x += width;
        if (_x >= Columns)
        {
            _x = Columns - 1;
            _pendingWrap = Modes.AutoWrap;
        }
    }

    private void AttachCombining(int cp)
    {
        int col = _pendingWrap ? _x : _x - 1;
        if (col < 0) return;
        var line = _screen[_y];
        if (line[col].Width == 0 && col > 0) col--;
        if (line[col].Rune == 0) return;
        line.AppendCombining(col, cp);
    }

    public void Execute(int c)
    {
        switch (c)
        {
            case 0x05: if (Answerback.Length > 0) Respond(Answerback); break;
            case 0x07: _bellPending = true; break;
            case 0x08:
                if (_x > 0) _x--;
                _pendingWrap = false;
                break;
            case 0x09: TabForward(1); break;
            case 0x0A: case 0x0B: case 0x0C:
                Index();
                if (Modes.LineFeedNewLine) CarriageReturn();
                _pendingWrap = false;
                break;
            case 0x0D: CarriageReturn(); break;
            case 0x0E: _gl = 1; break;
            case 0x0F: _gl = 0; break;
        }
    }

    private void TabForward(int n)
    {
        for (int k = 0; k < n; k++)
        {
            int x = _x + 1;
            while (x < Columns - 1 && !_tabStops[x]) x++;
            _x = Math.Min(x, Columns - 1);
        }
        _pendingWrap = false;
    }

    private void TabBackward(int n)
    {
        for (int k = 0; k < n; k++)
        {
            int x = _x - 1;
            while (x > 0 && !_tabStops[x]) x--;
            _x = Math.Max(x, 0);
        }
        _pendingWrap = false;
    }

    #endregion

    #region ESC

    public void EscDispatch(string inter, char final)
    {
        if (inter.Length == 0)
        {
            switch (final)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': Index(); _pendingWrap = false; break;
                case 'E': CarriageReturn(); Index(); break;
                case 'H': _tabStops[_x] = true; break;
                case 'M': ReverseIndex(); _pendingWrap = false; break;
                case 'c': FullReset(); break;
                case '=': Modes.ApplicationKeypad = true; _modesDirty = true; break;
                case '>': Modes.ApplicationKeypad = false; _modesDirty = true; break;
                case 'n': _gl = 2; break;
                case 'o': _gl = 3; break;
            }
            return;
        }
        if (inter == "#" && final == '8') { DecAlignmentTest(); return; }
        if (inter.Length == 1 && inter[0] is '(' or ')' or '*' or '+')
        {
            int slot = inter[0] switch { '(' => 0, ')' => 1, '*' => 2, _ => 3 };
            _g[slot] = final switch { '0' => Charset.DecSpecial, 'A' => Charset.Uk, _ => Charset.Ascii };
        }
    }

    private void DecAlignmentTest()
    {
        _scrollTop = 0; _scrollBottom = Rows - 1;
        foreach (var l in _screen)
            for (int x = 0; x < Columns; x++) l.Set(x, 'E', 1, default);
        SetCursor(0, 0);
    }

    private void SaveCursor()
    {
        var s = new SavedCursor
        {
            X = _x, Y = _y, PendingWrap = _pendingWrap, Attr = _attr, OriginMode = Modes.OriginMode, AutoWrap = Modes.AutoWrap,
            G0 = _g[0], G1 = _g[1], G2 = _g[2], G3 = _g[3], Gl = _gl, Valid = true,
        };
        if (IsAlternateScreen) _savedAlt = s; else _savedMain = s;
    }

    private void RestoreCursor()
    {
        var s = IsAlternateScreen ? _savedAlt : _savedMain;
        if (!s.Valid)
        {
            _x = 0; _y = 0; _pendingWrap = false; _attr = default; Modes.OriginMode = false;
            return;
        }
        _x = Math.Min(s.X, Columns - 1);
        _y = Math.Min(s.Y, Rows - 1);
        _pendingWrap = s.PendingWrap && _x == Columns - 1;
        _attr = s.Attr;
        Modes.OriginMode = s.OriginMode;
        Modes.AutoWrap = s.AutoWrap;
        _g[0] = s.G0; _g[1] = s.G1; _g[2] = s.G2; _g[3] = s.G3; _gl = s.Gl;
    }

    public void FullReset()
    {
        lock (SyncRoot)
        {
            _screen = _main;
            foreach (var l in _main) l.Reset(Columns, default);
            foreach (var l in _alt) l.Reset(Columns, default);
            _scrollback.Clear();
            _tabStops = NewTabStops(Columns);
            _x = _y = 0; _pendingWrap = false; _attr = default;
            _scrollTop = 0; _scrollBottom = Rows - 1;
            Array.Clear(_g); _gl = 0;
            _savedMain = default; _savedAlt = default;
            Modes.Reset();
            _modesDirty = true;
            Title = string.Empty; _titleDirty = true;
            ContentVersion++;
        }
    }

    private void SoftReset()
    {
        Modes.CursorVisible = true; Modes.OriginMode = false; Modes.AutoWrap = true; Modes.InsertMode = false;
        Modes.ApplicationCursorKeys = false; Modes.ApplicationKeypad = false;
        _scrollTop = 0; _scrollBottom = Rows - 1;
        _attr = default; Array.Clear(_g); _gl = 0;
        _savedMain = default; _savedAlt = default;
        _modesDirty = true;
    }

    #endregion
}
