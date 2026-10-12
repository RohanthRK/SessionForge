using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SessionForge.Terminal;

namespace SessionForge.App.Controls;

/// <summary>
/// High-performance terminal surface. Renders a <see cref="TerminalEmulator"/> with one cached DrawingVisual per row,
/// re-drawing only rows whose content, selection or highlight changed, at most once per frame.
/// </summary>
public sealed partial class TerminalControl : FrameworkElement
{
    private const double Padding = 4;

    private readonly VisualCollection _visuals;
    private readonly List<DrawingVisual> _rows = [];
    private readonly List<Drawing?> _rowDrawings = [];
    // Frozen drawings per line object: when output scrolls, rows just re-reference existing drawings.
    private Dictionary<TerminalLine, (LineKey Key, Drawing Drawing)> _lineCache = new(ReferenceEqualityComparer.Instance);
    private Dictionary<TerminalLine, (LineKey Key, Drawing Drawing)> _nextLineCache = new(ReferenceEqualityComparer.Instance);
    private readonly DrawingVisual _cursorVisual = new();
    private readonly DrawingVisual _flashVisual = new();
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _resizeTimer;
    private TerminalEmulator? _emulator;
    private TerminalFontSet? _fonts;
    private TerminalPalette? _palette;
    private int _dirty = 1;
    private bool _renderHooked;
    private bool _cursorOn = true;
    private int _viewportTop;          // index into emulator.GetLine numbering
    private bool _follow = true;
    private long _paletteVersion;
    private DateTime _syncSince;

    private readonly record struct LineKey(int Version, int SelStart, int SelEnd, int MatchStart, int MatchEnd, long PaletteVersion, bool Reverse, int Columns, bool Keywords);

    private static readonly Drawing EmptyDrawing = CreateEmpty();
    private static Drawing CreateEmpty() { var g = new DrawingGroup(); g.Freeze(); return g; }

    public TerminalControl()
    {
        _visuals = new VisualCollection(this);
        _visuals.Add(_cursorVisual);
        _visuals.Add(_flashVisual);
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = System.Windows.Input.Cursors.IBeam;
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        _blinkTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) => BlinkTick(), Dispatcher);
        _resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => ApplyPendingResize(), Dispatcher);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) { InvalidateTerminal(); ScheduleResize(); } };
    }

    #region Public API

    public TerminalEmulator? Emulator
    {
        get => _emulator;
        set
        {
            if (_emulator != null) _emulator.Updated -= OnEmulatorUpdated;
            _emulator = value;
            if (_emulator != null)
            {
                _emulator.Updated += OnEmulatorUpdated;
                _palette = new TerminalPalette(_emulator.Theme, BoldIsBright);
                _paletteVersion++;
            }
            ClearSelection();
            _follow = true;
            InvalidateTerminal();
        }
    }

    /// <summary>Sends encoded input to the host (set by the session host).</summary>
    public Action<string>? SendInput { get; set; }

    /// <summary>Raised when the grid size (columns, rows, pixel width, pixel height) changes.</summary>
    public event Action<int, int, int, int>? GridResized;

    /// <summary>Raised when the viewport moves or the scrollback grows (for an external scrollbar).</summary>
    public event Action? ViewportChanged;

    public event Action? BellRang;

    public string FontFamilyList { get; private set; } = "Cascadia Mono, Consolas";
    public double TerminalFontSize { get; private set; } = 13;
    public bool BoldIsBright { get; set; } = true;
    public bool CopyOnSelect { get; set; } = true;
    public bool RightClickPastes { get; set; } = true;
    public string WordDelimiters { get; set; } = TerminalEmulator.DefaultWordDelimiters;
    public bool BlinkCursor { get; set; } = true;
    public bool VisualBell { get; set; } = true;

    private bool _keywordHighlighting;
    /// <summary>Colour error/warning/success keywords and IP addresses in default-coloured output.</summary>
    public bool KeywordHighlighting
    {
        get => _keywordHighlighting;
        set { if (_keywordHighlighting == value) return; _keywordHighlighting = value; InvalidateTerminal(); }
    }
    public KeyboardOptions KeyboardOptions { get; } = new();

    /// <summary>Paste confirmation hook: returns the text to paste (possibly edited) or null to cancel.</summary>
    public Func<string, string?>? ConfirmPaste { get; set; }

    /// <summary>Called for Ctrl+click on a hyperlink or URL.</summary>
    public Action<string>? OpenLink { get; set; }

    public Func<System.Windows.Controls.ContextMenu?>? ContextMenuFactory { get; set; }

    public int Columns => _emulator?.Columns ?? 0;
    public int Rows => _emulator?.Rows ?? 0;
    public double CellWidth => _fonts?.CellWidth ?? 8;
    public double CellHeight => _fonts?.CellHeight ?? 16;

    /// <summary>Line index (in emulator numbering) of the top visible row.</summary>
    public int ViewportTop => _viewportTop;
    public bool IsFollowingOutput => _follow;

    public void SetFont(string familyList, double size)
    {
        FontFamilyList = familyList;
        TerminalFontSize = Math.Clamp(size, 6, 72);
        _fonts = null;
        InvalidateTerminal();
        InvalidateMeasure();
        ScheduleResize();
    }

    public void Zoom(double delta) => SetFont(FontFamilyList, TerminalFontSize + delta);

    public void SetTheme(TerminalTheme theme)
    {
        if (_emulator != null) _emulator.Theme = theme;
        _palette = new TerminalPalette(theme, BoldIsBright);
        _paletteVersion++;
        InvalidateTerminal();
        InvalidateVisual();
    }

    public void InvalidateTerminal()
    {
        Interlocked.Exchange(ref _dirty, 1);
        if (!_renderHooked && IsLoaded) Attach();
    }

    public void ScrollToBottom()
    {
        _follow = true;
        InvalidateTerminal();
    }

    public void ScrollBy(int lines)
    {
        if (_emulator == null) return;
        int max;
        lock (_emulator.SyncRoot) max = _emulator.ScrollbackCount;
        int top = Math.Clamp(_viewportTop + lines, 0, max);
        _viewportTop = top;
        _follow = top >= max;
        InvalidateTerminal();
        ViewportChanged?.Invoke();
    }

    public void ScrollTo(int top)
    {
        if (_emulator == null) return;
        int max;
        lock (_emulator.SyncRoot) max = _emulator.ScrollbackCount;
        _viewportTop = Math.Clamp(top, 0, max);
        _follow = _viewportTop >= max;
        InvalidateTerminal();
        ViewportChanged?.Invoke();
    }

    /// <summary>Scrolls so that a buffer line is visible (roughly centred).</summary>
    public void Reveal(int lineIndex)
    {
        if (_emulator == null) return;
        if (lineIndex >= _viewportTop && lineIndex < _viewportTop + Rows) { InvalidateTerminal(); return; }
        ScrollTo(lineIndex - Rows / 2);
    }

    #endregion

    #region Visual tree

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    private void EnsureFonts()
    {
        if (_fonts != null) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _fonts = new TerminalFontSet(FontFamilyList, TerminalFontSize, dpi);
        _paletteVersion++; // force row redraw with new metrics
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _fonts = null;
        InvalidateTerminal();
        ScheduleResize();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fill whatever space we are given.
        return new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Full background (default terminal background) — also makes the whole surface hit-testable.
        var bg = _palette?.Brush(_palette.DefaultBackground) ?? Brushes.Black;
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ScheduleResize();
    }

    private void ScheduleResize()
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void ApplyPendingResize()
    {
        _resizeTimer.Stop();
        if (_emulator == null || ActualWidth < 10 || ActualHeight < 10) return;
        EnsureFonts();
        int cols = Math.Max(2, (int)((ActualWidth - 2 * Padding) / _fonts!.CellWidth));
        int rows = Math.Max(1, (int)((ActualHeight - 2 * Padding) / _fonts.CellHeight));
        if (cols != _emulator.Columns || rows != _emulator.Rows)
        {
            _emulator.Resize(cols, rows);
            ClearSelection();
            _follow = true;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        GridResized?.Invoke(cols, rows, (int)(cols * _fonts.CellWidth * dpi.DpiScaleX), (int)(rows * _fonts.CellHeight * dpi.DpiScaleY));
        InvalidateTerminal();
    }

    /// <summary>Forces the current size to be reported again (after the transport connects).</summary>
    public void ReportSize() => ApplyPendingResize();

    private void Attach()
    {
        if (_renderHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderHooked = true;
        if (BlinkCursor) _blinkTimer.Start();
        ScheduleResize();
    }

    private void Detach()
    {
        if (!_renderHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderHooked = false;
        _blinkTimer.Stop();
    }

    private void OnEmulatorUpdated() => Interlocked.Exchange(ref _dirty, 1);

    private void OnRendering(object? sender, EventArgs e)
    {
        // Background tabs keep their dirty flag and render when shown again.
        if (_emulator == null || !IsVisible || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        // Synchronized output (DEC 2026): hold frames until the app finishes, but never longer than 250 ms.
        if (_emulator.Modes.SynchronizedOutput)
        {
            if (_syncSince == default) _syncSince = DateTime.UtcNow;
            if (DateTime.UtcNow - _syncSince < TimeSpan.FromMilliseconds(250)) { Interlocked.Exchange(ref _dirty, 1); return; }
        }
        _syncSince = default;
        RenderFrame();
    }

    private void RenderFrame()
    {
        var emu = _emulator!;
        EnsureFonts();
        _palette ??= new TerminalPalette(emu.Theme, BoldIsBright);
        var fonts = _fonts!;
        bool viewportChanged;

        lock (emu.SyncRoot)
        {
            int scrollback = emu.ScrollbackCount;
            int oldTop = _viewportTop;
            if (_follow) _viewportTop = scrollback;
            _viewportTop = Math.Clamp(_viewportTop, 0, scrollback);
            viewportChanged = oldTop != _viewportTop || _lastScrollback != scrollback;
            _lastScrollback = scrollback;

            int rows = emu.Rows;
            while (_rows.Count < rows)
            {
                var v = new DrawingVisual();
                _rows.Add(v);
                _rowDrawings.Add(null);
                _visuals.Insert(_visuals.Count - 2, v);
            }
            while (_rows.Count > rows)
            {
                int last = _rows.Count - 1;
                _visuals.Remove(_rows[last]);
                _rows.RemoveAt(last);
                _rowDrawings.RemoveAt(last);
            }

            var sel = NormalizedSelection(emu);
            var match = _searchMatch;
            int total = emu.TotalLines;
            var flags = emu.Modes.ReverseVideo ? RowPainter.RowFlags.ReverseVideo : RowPainter.RowFlags.None;
            _nextLineCache.Clear();
            for (int r = 0; r < rows; r++)
            {
                int idx = _viewportTop + r;
                var line = idx < total ? emu.GetLine(idx) : null;
                Drawing drawing = EmptyDrawing;
                if (line != null)
                {
                    var (s0, s1) = SelectionColumns(sel, idx, emu.Columns);
                    var (m0, m1) = MatchColumns(match, idx);
                    var key = new LineKey(line.Version, s0, s1, m0, m1, _paletteVersion, emu.Modes.ReverseVideo, emu.Columns, KeywordHighlighting);
                    if (_lineCache.TryGetValue(line, out var cached) && cached.Key == key) drawing = cached.Drawing;
                    else
                    {
                        var group = new DrawingGroup();
                        using (var gdc = group.Open()) RowPainter.Paint(gdc, line, 0, fonts, _palette, flags, s0, s1, m0, m1, emu.Columns, KeywordHighlighting);
                        group.Freeze();
                        drawing = group;
                    }
                    _nextLineCache[line] = (key, drawing);
                }
                var visual = _rows[r];
                visual.Offset = new Vector(Padding, Padding + r * fonts.CellHeight);
                if (!ReferenceEquals(_rowDrawings[r], drawing))
                {
                    _rowDrawings[r] = drawing;
                    using var dc = visual.RenderOpen();
                    dc.DrawDrawing(drawing);
                }
            }
            (_lineCache, _nextLineCache) = (_nextLineCache, _lineCache);
            DrawCursor(emu, fonts);
        }
        if (viewportChanged) ViewportChanged?.Invoke();
    }

    private int _lastScrollback = -1;

    private void DrawCursor(TerminalEmulator emu, TerminalFontSet fonts)
    {
        using var dc = _cursorVisual.RenderOpen();
        if (!emu.Modes.CursorVisible) return;
        int cursorLine = emu.ScrollbackCount + emu.CursorY;
        int row = cursorLine - _viewportTop;
        if (row < 0 || row >= emu.Rows) return;
        bool focused = IsKeyboardFocusWithin;
        if (focused && BlinkCursor && emu.Modes.CursorBlink && !_cursorOn) return;

        int col = emu.CursorX;
        var line = emu.GetScreenLine(emu.CursorY);
        ref readonly var cell = ref line[col];
        int width = cell.Width == 2 ? 2 : 1;
        double x = Padding + col * fonts.CellWidth, y = Padding + row * fonts.CellHeight;
        double w = fonts.CellWidth * width, h = fonts.CellHeight;
        var brush = _palette!.Brush(_palette.CursorColor);

        if (!focused)
        {
            var pen = new Pen(brush, 1);
            pen.Freeze();
            dc.DrawRectangle(null, pen, new Rect(x + 0.5, y + 0.5, w - 1, h - 1));
            return;
        }
        switch (emu.Modes.CursorShape)
        {
            case CursorShape.Bar:
                dc.DrawRectangle(brush, null, new Rect(x, y, Math.Max(1.5, fonts.LineThickness * 2), h));
                break;
            case CursorShape.Underline:
                dc.DrawRectangle(brush, null, new Rect(x, y + h - Math.Max(2, fonts.LineThickness * 2), w, Math.Max(2, fonts.LineThickness * 2)));
                break;
            default:
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                // Redraw the character under the block in the background colour for contrast.
                if (cell.Rune > ' ' && fonts.TryGetGlyph(cell.Rune, cell.Attributes.Has(CellFlags.Bold), cell.Attributes.Has(CellFlags.Italic), out var face, out var glyph))
                {
                    double adv = face.AdvanceWidths[glyph] * fonts.FontSize;
                    var gr = new GlyphRun(face, 0, false, fonts.FontSize, (float)fonts.PixelsPerDip, [glyph],
                        new Point(x + Math.Max(0, (w - adv) / 2), y + fonts.Baseline), [w], null, null, null, null, null, null);
                    dc.DrawGlyphRun(_palette.Brush(_palette.DefaultBackground), gr);
                }
                break;
        }
    }

    private void BlinkTick()
    {
        _cursorOn = !_cursorOn;
        if (_emulator == null || _fonts == null) return;
        lock (_emulator.SyncRoot) DrawCursor(_emulator, _fonts);
    }

    /// <summary>Restart the blink phase so the cursor is visible right after typing.</summary>
    private void ResetBlink()
    {
        _cursorOn = true;
        if (BlinkCursor) { _blinkTimer.Stop(); _blinkTimer.Start(); }
    }

    public void Bell()
    {
        BellRang?.Invoke();
        if (!VisualBell || _palette == null) return;
        using (var dc = _flashVisual.RenderOpen())
        {
            var b = _palette.Brush(0x30FFFFFF);
            dc.DrawRectangle(b, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            using var dc = _flashVisual.RenderOpen();
        }, Dispatcher);
        t.Start();
    }

    #endregion

    /// <summary>Converts a point in control coordinates to a buffer position (line index, column).</summary>
    public BufferPoint HitTestCell(Point p, bool clampToGrid = true)
    {
        var f = _fonts ?? new TerminalFontSet(FontFamilyList, TerminalFontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        int col = (int)Math.Floor((p.X - Padding) / f.CellWidth);
        int row = (int)Math.Floor((p.Y - Padding) / f.CellHeight);
        if (clampToGrid)
        {
            col = Math.Clamp(col, 0, Math.Max(0, Columns - 1));
            row = Math.Clamp(row, -1, Rows);
        }
        return new BufferPoint(_viewportTop + row, col);
    }

    /// <summary>Screen (row, col) relative to the visible grid, for mouse reporting.</summary>
    private (int Col, int Row) GridCell(Point p)
    {
        var f = _fonts!;
        int col = Math.Clamp((int)Math.Floor((p.X - Padding) / f.CellWidth), 0, Math.Max(0, Columns - 1));
        int row = Math.Clamp((int)Math.Floor((p.Y - Padding) / f.CellHeight), 0, Math.Max(0, Rows - 1));
        return (col, row);
    }
}
