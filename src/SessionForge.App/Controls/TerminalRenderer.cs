using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SessionForge.Terminal;

namespace SessionForge.App.Controls;

/// <summary>
/// Font metrics and glyph lookup for the terminal grid. Cell sizes are snapped to device pixels so text and
/// backgrounds line up exactly. Characters missing from the primary font fall back to a list of system fonts.
/// </summary>
public sealed class TerminalFontSet
{
    private static readonly string[] FallbackFamilies =
        ["Cascadia Mono", "Consolas", "Segoe UI Symbol", "Segoe UI Emoji", "MS Gothic", "SimSun", "Malgun Gothic", "Nirmala UI", "Segoe UI Historic", "Arial Unicode MS", "Segoe UI"];

    private readonly GlyphTypeface[] _primary = new GlyphTypeface[4]; // regular, bold, italic, bold-italic
    private readonly List<GlyphTypeface> _fallbacks = [];
    private readonly Dictionary<int, (GlyphTypeface Face, ushort Glyph)?> _fallbackCache = new();

    public TerminalFontSet(string familyList, double fontSize, double pixelsPerDip)
    {
        FontSize = fontSize;
        PixelsPerDip = pixelsPerDip;
        var families = familyList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        GlyphTypeface? regular = null;
        foreach (var name in families)
        {
            var tf = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out var g) && g.CharacterToGlyphMap.ContainsKey('M'))
            {
                regular = g;
                Family = new FontFamily(name);
                break;
            }
        }
        if (regular == null)
        {
            Family = new FontFamily("Courier New");
            new Typeface(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out regular);
        }
        _primary[0] = regular!;
        _primary[1] = Face(FontStyles.Normal, FontWeights.Bold) ?? regular!;
        _primary[2] = Face(FontStyles.Italic, FontWeights.Normal) ?? regular!;
        _primary[3] = Face(FontStyles.Italic, FontWeights.Bold) ?? _primary[1];
        foreach (var f in FallbackFamilies)
        {
            var tf = new Typeface(new FontFamily(f), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out var g)) _fallbacks.Add(g);
        }

        // Metrics, snapped to whole device pixels.
        double dpi = pixelsPerDip;
        var map = regular!.CharacterToGlyphMap;
        double advance = map.TryGetValue('M', out var mg) ? regular.AdvanceWidths[mg] : 0.6;
        CellWidth = Math.Max(1, Math.Round(advance * fontSize * dpi)) / dpi;
        double lineHeight = regular.Height * fontSize;
        CellHeight = Math.Max(1, Math.Ceiling(lineHeight * 1.08 * dpi)) / dpi;
        // Centre the font's ascent/descent within the (slightly taller) cell.
        double extra = (CellHeight - lineHeight) / 2;
        Baseline = Math.Round((regular.Baseline * fontSize + extra) * dpi) / dpi;
        UnderlineY = Math.Round((Baseline + Math.Max(1, -regular.UnderlinePosition * fontSize)) * dpi) / dpi;
        LineThickness = Math.Max(1, Math.Round(regular.UnderlineThickness * fontSize * dpi)) / dpi;
        StrikeY = Math.Round((Baseline - regular.StrikethroughPosition * fontSize) * dpi) / dpi;

        Typeface = new Typeface(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    private GlyphTypeface? Face(FontStyle style, FontWeight weight)
    {
        var tf = new Typeface(Family, style, weight, FontStretches.Normal);
        return tf.TryGetGlyphTypeface(out var g) ? g : null;
    }

    public FontFamily Family { get; private set; } = null!;
    public Typeface Typeface { get; }
    public double FontSize { get; }
    public double PixelsPerDip { get; }
    public double CellWidth { get; }
    public double CellHeight { get; }
    public double Baseline { get; }
    public double UnderlineY { get; }
    public double StrikeY { get; }
    public double LineThickness { get; }

    public GlyphTypeface Primary(bool bold, bool italic) => _primary[(bold ? 1 : 0) + (italic ? 2 : 0)];

    /// <summary>Finds a typeface + glyph for a code point (primary style first, then fallbacks).</summary>
    public bool TryGetGlyph(int cp, bool bold, bool italic, out GlyphTypeface face, out ushort glyph)
    {
        face = Primary(bold, italic);
        if (face.CharacterToGlyphMap.TryGetValue(cp, out glyph)) return true;
        if (!_fallbackCache.TryGetValue(cp, out var hit))
        {
            hit = null;
            foreach (var f in _fallbacks)
                if (f.CharacterToGlyphMap.TryGetValue(cp, out var g)) { hit = (f, g); break; }
            _fallbackCache[cp] = hit;
        }
        if (hit is { } h) { face = h.Face; glyph = h.Glyph; return true; }
        // Replacement character from the primary font, if it has one.
        face = Primary(bold, italic);
        return face.CharacterToGlyphMap.TryGetValue(0xFFFD, out glyph) || face.CharacterToGlyphMap.TryGetValue('?', out glyph);
    }
}

/// <summary>Colour resolution for cells (palette, bold-as-bright, dim, inverse, selection), with a frozen brush cache.</summary>
public sealed class TerminalPalette
{
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    public TerminalPalette(TerminalTheme theme, bool boldIsBright)
    {
        Theme = theme;
        BoldIsBright = boldIsBright;
    }

    public TerminalTheme Theme { get; }
    public bool BoldIsBright { get; }

    public static uint Pack(Rgb c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    public uint Resolve(TerminalColor c, bool foreground, bool bold)
    {
        switch (c.Kind)
        {
            case ColorKind.Indexed:
                int i = c.Index;
                if (foreground && bold && BoldIsBright && i < 8) i += 8;
                return Pack(Theme.Palette(i));
            case ColorKind.Rgb:
                return 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            default:
                return Pack(foreground ? Theme.Foreground : Theme.Background);
        }
    }

    public static uint Blend(uint a, uint b, double t)
    {
        byte Mix(int sa, int sb) => (byte)Math.Round(sa + (sb - sa) * t);
        return 0xFF000000u | ((uint)Mix((int)(a >> 16) & 255, (int)(b >> 16) & 255) << 16) | ((uint)Mix((int)(a >> 8) & 255, (int)(b >> 8) & 255) << 8) | Mix((int)a & 255, (int)b & 255);
    }

    public SolidColorBrush Brush(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var b))
        {
            b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            _brushes[argb] = b;
        }
        return b;
    }

    public uint DefaultBackground => Pack(Theme.Background);
    public uint DefaultForeground => Pack(Theme.Foreground);
    public uint CursorColor => Pack(Theme.Cursor);
    public uint SelectionColor => Pack(Theme.SelectionBackground);
}

/// <summary>Draws one terminal row into a DrawingContext.</summary>
public static class RowPainter
{
    [Flags]
    public enum RowFlags { None = 0, ReverseVideo = 1 }

    private struct CellStyle
    {
        public uint Fg, Bg;
        public bool Bold, Italic, Underline, DoubleUnderline, Strike, Overline, Hidden, Link;
    }

    /// <param name="selStart">First selected column on this row, or -1.</param>
    /// <param name="selEnd">Last selected column (inclusive).</param>
    /// <param name="matchStart">First column of the current search match on this row, or -1.</param>
    public static void Paint(DrawingContext dc, TerminalLine line, double y, TerminalFontSet fonts, TerminalPalette pal,
        RowFlags flags, int selStart, int selEnd, int matchStart, int matchEnd, int columns, bool keywordHighlighting = false)
    {
        double cw = fonts.CellWidth, ch = fonts.CellHeight;
        int n = Math.Min(columns, line.Length);
        var cells = line.Cells;
        bool reverse = (flags & RowFlags.ReverseVideo) != 0;
        uint defBg = pal.DefaultBackground;
        var styles = new CellStyle[n];
        var keywordColors = keywordHighlighting ? KeywordHighlighter.Compute(line, columns) : null;

        for (int x = 0; x < n; x++)
        {
            ref readonly var c = ref cells[x];
            var a = c.Attributes;
            bool bold = a.Has(CellFlags.Bold);
            uint fg = pal.Resolve(a.Foreground, true, bold);
            uint bg = pal.Resolve(a.Background, false, false);
            bool inverse = a.Has(CellFlags.Inverse) ^ reverse;
            if (keywordColors != null && keywordColors[x] != 0 && !inverse) fg = keywordColors[x];
            if (inverse) (fg, bg) = (bg, fg);
            if (a.Has(CellFlags.Dim)) fg = TerminalPalette.Blend(fg, bg, 0.45);
            bool selected = x >= selStart && x <= selEnd && selStart >= 0;
            bool match = x >= matchStart && x <= matchEnd && matchStart >= 0;
            if (match) { bg = 0xFFE5A50A; fg = 0xFF111111; }
            else if (selected) { bg = pal.SelectionColor; }
            styles[x] = new CellStyle
            {
                Fg = fg, Bg = bg, Bold = bold, Italic = a.Has(CellFlags.Italic),
                Underline = a.Has(CellFlags.Underline) || a.Has(CellFlags.CurlyUnderline), DoubleUnderline = a.Has(CellFlags.DoubleUnderline),
                Strike = a.Has(CellFlags.Strikethrough), Overline = a.Has(CellFlags.Overline), Hidden = a.Has(CellFlags.Hidden), Link = a.LinkId != 0,
            };
        }

        // 1. Backgrounds (merge runs; the default background is already painted by the control).
        int run = 0;
        while (run < n)
        {
            uint bg = styles[run].Bg;
            int end = run + 1;
            while (end < n && styles[end].Bg == bg) end++;
            if (bg != defBg) dc.DrawRectangle(pal.Brush(bg), null, new Rect(run * cw, y, (end - run) * cw, ch));
            run = end;
        }

        // 2. Glyphs, batched into GlyphRuns of identical style and typeface.
        var glyphs = new List<ushort>(n);
        var advances = new List<double>(n);
        var offsets = new List<Point>(n);
        GlyphTypeface? runFace = null;
        uint runFg = 0;
        double runX = 0, runEnd = 0;
        bool needOffsets = false;

        void Flush()
        {
            if (glyphs.Count == 0 || runFace == null) return;
            var origin = new Point(runX, y + fonts.Baseline);
            var gr = new GlyphRun(runFace, 0, false, fonts.FontSize, (float)fonts.PixelsPerDip, glyphs.ToArray(), origin,
                advances.ToArray(), needOffsets ? offsets.ToArray() : null, null, null, null, null, XmlLanguage);
            dc.DrawGlyphRun(pal.Brush(runFg), gr);
            glyphs.Clear(); advances.Clear(); offsets.Clear();
            needOffsets = false;
        }

        for (int x = 0; x < n; x++)
        {
            ref readonly var c = ref cells[x];
            if (c.Width == 0) continue;
            var st = styles[x];
            int cp = c.Rune;
            double cellAdvance = cw * Math.Max((int)c.Width, 1);
            string? combining = line.GetCombining(x);
            if (cp == 0 || cp == ' ' || st.Hidden || combining != null || IsBoxOrBlock(cp))
            {
                Flush();
                runFace = null;
                if (cp != 0 && cp != ' ' && !st.Hidden)
                {
                    if (IsBoxOrBlock(cp) && combining == null && DrawBoxChar(dc, cp, x * cw, y, cw, ch, pal.Brush(st.Fg), fonts)) continue;
                    DrawComplex(dc, char.ConvertFromUtf32(cp) + combining, x * cw, y, cellAdvance, st, fonts, pal);
                }
                continue;
            }
            if (!fonts.TryGetGlyph(cp, st.Bold, st.Italic, out var face, out var glyph))
            {
                Flush(); runFace = null;
                continue;
            }
            // New run on style/face change, or when blank cells were skipped since the last glyph.
            if (runFace != face || runFg != st.Fg || (glyphs.Count > 0 && Math.Abs(runEnd - x * cw) > 0.01))
            {
                Flush();
                runFace = face;
                runFg = st.Fg;
                runX = x * cw;
                runEnd = runX;
            }
            double glyphAdvance = face.AdvanceWidths[glyph] * fonts.FontSize;
            double offset = (cellAdvance - glyphAdvance) / 2;
            if (Math.Abs(offset) > 0.01 && (c.Width == 2 || face != fonts.Primary(st.Bold, st.Italic))) needOffsets = true; else offset = 0;
            glyphs.Add(glyph);
            advances.Add(cellAdvance);
            offsets.Add(new Point(offset, 0));
            runEnd += cellAdvance;
        }
        Flush();

        // 3. Decorations.
        for (int x = 0; x < n;)
        {
            var st = styles[x];
            if (!(st.Underline || st.DoubleUnderline || st.Strike || st.Overline || st.Link)) { x++; continue; }
            int end = x + 1;
            while (end < n && SameDecoration(styles[end], st)) end++;
            var brush = pal.Brush(st.Fg);
            double x0 = x * cw, w = (end - x) * cw, t = fonts.LineThickness;
            if (st.Underline || st.DoubleUnderline) dc.DrawRectangle(brush, null, new Rect(x0, Math.Min(y + ch - t, y + fonts.UnderlineY), w, t));
            if (st.DoubleUnderline) dc.DrawRectangle(brush, null, new Rect(x0, Math.Min(y + ch - t, y + fonts.UnderlineY + 2 * t), w, t));
            if (st.Strike) dc.DrawRectangle(brush, null, new Rect(x0, y + fonts.StrikeY, w, t));
            if (st.Overline) dc.DrawRectangle(brush, null, new Rect(x0, y, w, t));
            if (st.Link && !st.Underline)
            {
                var dotted = pal.Brush(TerminalPalette.Blend(st.Fg, st.Bg, 0.5));
                for (double dx = x0; dx < x0 + w; dx += 3 * t) dc.DrawRectangle(dotted, null, new Rect(dx, Math.Min(y + ch - t, y + fonts.UnderlineY), t, t));
            }
            x = end;
        }
    }

    private static readonly XmlLanguageWrapper LanguageHolder = new();
    private static System.Windows.Markup.XmlLanguage XmlLanguage => LanguageHolder.Value;

    private sealed class XmlLanguageWrapper
    {
        public System.Windows.Markup.XmlLanguage Value { get; } = System.Windows.Markup.XmlLanguage.GetLanguage("en-us");
    }

    private static bool SameDecoration(in CellStyle a, in CellStyle b) =>
        a.Fg == b.Fg && a.Underline == b.Underline && a.DoubleUnderline == b.DoubleUnderline && a.Strike == b.Strike && a.Overline == b.Overline && a.Link == b.Link;

    private static void DrawComplex(DrawingContext dc, string text, double x, double y, double width, in CellStyle st, TerminalFontSet fonts, TerminalPalette pal)
    {
        var tf = new Typeface(fonts.Family, st.Italic ? FontStyles.Italic : FontStyles.Normal, st.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, fonts.FontSize, pal.Brush(st.Fg), fonts.PixelsPerDip);
        double dx = Math.Max(0, (width - ft.WidthIncludingTrailingWhitespace) / 2);
        dc.PushClip(new RectangleGeometry(new Rect(x, y, width, fonts.CellHeight)));
        dc.DrawText(ft, new Point(x + dx, y + fonts.Baseline - ft.Baseline));
        dc.Pop();
    }

    /// <summary>Box drawing (U+2500–257F) and block elements (U+2580–259F) are drawn geometrically so lines connect seamlessly.</summary>
    public static bool IsBoxOrBlock(int cp) => cp is >= 0x2500 and <= 0x259F;

    private static bool DrawBoxChar(DrawingContext dc, int cp, double x, double y, double w, double h, Brush brush, TerminalFontSet fonts)
    {
        double px = 1 / fonts.PixelsPerDip;
        double light = Math.Max(px, Math.Round(fonts.LineThickness / px) * px);
        double heavy = light * 2;
        double cx = x + Math.Floor(w / 2 / px) * px, cy = y + Math.Floor(h / 2 / px) * px;

        void H(double x0, double x1, double t) => dc.DrawRectangle(brush, null, new Rect(x0, cy - Math.Floor(t / 2 / px) * px, Math.Max(0, x1 - x0), t));
        void V(double y0, double y1, double t) => dc.DrawRectangle(brush, null, new Rect(cx - Math.Floor(t / 2 / px) * px, y0, t, Math.Max(0, y1 - y0)));
        void Block(double fx0, double fy0, double fx1, double fy1, double alpha = 1)
        {
            var r = new Rect(x + w * fx0, y + h * fy0, w * (fx1 - fx0), h * (fy1 - fy0));
            if (alpha >= 1) dc.DrawRectangle(brush, null, r);
            else { dc.PushOpacity(alpha); dc.DrawRectangle(brush, null, r); dc.Pop(); }
        }

        // Light/heavy lines: encode arms as (left, right, up, down) thickness: 0 none, 1 light, 2 heavy.
        (int l, int r, int u, int d) arms = cp switch
        {
            0x2500 => (1, 1, 0, 0), 0x2501 => (2, 2, 0, 0), 0x2502 => (0, 0, 1, 1), 0x2503 => (0, 0, 2, 2),
            0x250C => (0, 1, 0, 1), 0x250F => (0, 2, 0, 2), 0x2510 => (1, 0, 0, 1), 0x2513 => (2, 0, 0, 2),
            0x2514 => (0, 1, 1, 0), 0x2517 => (0, 2, 2, 0), 0x2518 => (1, 0, 1, 0), 0x251B => (2, 0, 2, 0),
            0x251C => (0, 1, 1, 1), 0x2523 => (0, 2, 2, 2), 0x2524 => (1, 0, 1, 1), 0x252B => (2, 0, 2, 2),
            0x252C => (1, 1, 0, 1), 0x2533 => (2, 2, 0, 2), 0x2534 => (1, 1, 1, 0), 0x253B => (2, 2, 2, 0),
            0x253C => (1, 1, 1, 1), 0x254B => (2, 2, 2, 2),
            0x2574 => (1, 0, 0, 0), 0x2575 => (0, 0, 1, 0), 0x2576 => (0, 1, 0, 0), 0x2577 => (0, 0, 0, 1),
            0x2578 => (2, 0, 0, 0), 0x2579 => (0, 0, 2, 0), 0x257A => (0, 2, 0, 0), 0x257B => (0, 0, 0, 2),
            _ => (-1, -1, -1, -1),
        };
        if (arms.l >= 0)
        {
            double T(int k) => k == 2 ? heavy : light;
            int vmax = Math.Max(arms.u, arms.d), hmax = Math.Max(arms.l, arms.r);
            double vHalf = vmax > 0 ? T(vmax) / 2 : 0, hHalf = hmax > 0 ? T(hmax) / 2 : 0;
            if (arms.l > 0) H(x, cx + vHalf, T(arms.l));
            if (arms.r > 0) H(cx - vHalf, x + w, T(arms.r));
            if (arms.u > 0) V(y, cy + hHalf, T(arms.u));
            if (arms.d > 0) V(cy - hHalf, y + h, T(arms.d));
            return true;
        }

        switch (cp)
        {
            // Double lines
            case >= 0x2550 and <= 0x256C: return DrawDouble(dc, cp, x, y, w, h, brush, light, cx, cy);
            // Rounded corners: approximate with square corners
            case 0x256D: H(cx, x + w, light); V(cy, y + h, light); return true;
            case 0x256E: H(x, cx + light, light); V(cy, y + h, light); return true;
            case 0x256F: H(x, cx + light, light); V(y, cy + light, light); return true;
            case 0x2570: H(cx, x + w, light); V(y, cy + light, light); return true;
            // Block elements
            case 0x2580: Block(0, 0, 1, 0.5); return true;
            case >= 0x2581 and <= 0x2588: Block(0, 1 - (cp - 0x2580) / 8.0, 1, 1); return true;
            case >= 0x2589 and <= 0x258F: Block(0, 0, 1 - (cp - 0x2588) / 8.0, 1); return true;
            case 0x2590: Block(0.5, 0, 1, 1); return true;
            case 0x2591: Block(0, 0, 1, 1, 0.25); return true;
            case 0x2592: Block(0, 0, 1, 1, 0.5); return true;
            case 0x2593: Block(0, 0, 1, 1, 0.75); return true;
            case 0x2594: Block(0, 0, 1, 1 / 8.0); return true;
            case 0x2595: Block(7 / 8.0, 0, 1, 1); return true;
            case 0x2596: Block(0, 0.5, 0.5, 1); return true;
            case 0x2597: Block(0.5, 0.5, 1, 1); return true;
            case 0x2598: Block(0, 0, 0.5, 0.5); return true;
            case 0x2599: Block(0, 0, 0.5, 1); Block(0.5, 0.5, 1, 1); return true;
            case 0x259A: Block(0, 0, 0.5, 0.5); Block(0.5, 0.5, 1, 1); return true;
            case 0x259B: Block(0, 0, 1, 0.5); Block(0, 0.5, 0.5, 1); return true;
            case 0x259C: Block(0, 0, 1, 0.5); Block(0.5, 0.5, 1, 1); return true;
            case 0x259D: Block(0.5, 0, 1, 0.5); return true;
            case 0x259E: Block(0.5, 0, 1, 0.5); Block(0, 0.5, 0.5, 1); return true;
            case 0x259F: Block(0.5, 0, 1, 1); Block(0, 0.5, 0.5, 1); return true;
        }
        return false; // dashed lines etc.: render with the font
    }

    /// <summary>Double-line box drawing (U+2550–256C): draws two parallel strokes per arm.</summary>
    private static bool DrawDouble(DrawingContext dc, int cp, double x, double y, double w, double h, Brush b, double t, double cx, double cy)
    {
        // arms: 0 none, 1 single, 2 double
        (int l, int r, int u, int d) a = cp switch
        {
            0x2550 => (2, 2, 0, 0), 0x2551 => (0, 0, 2, 2),
            0x2552 => (0, 2, 0, 1), 0x2553 => (0, 1, 0, 2), 0x2554 => (0, 2, 0, 2),
            0x2555 => (2, 0, 0, 1), 0x2556 => (1, 0, 0, 2), 0x2557 => (2, 0, 0, 2),
            0x2558 => (0, 2, 1, 0), 0x2559 => (0, 1, 2, 0), 0x255A => (0, 2, 2, 0),
            0x255B => (2, 0, 1, 0), 0x255C => (1, 0, 2, 0), 0x255D => (2, 0, 2, 0),
            0x255E => (0, 2, 1, 1), 0x255F => (0, 1, 2, 2), 0x2560 => (0, 2, 2, 2),
            0x2561 => (2, 0, 1, 1), 0x2562 => (1, 0, 2, 2), 0x2563 => (2, 0, 2, 2),
            0x2564 => (2, 2, 0, 1), 0x2565 => (1, 1, 0, 2), 0x2566 => (2, 2, 0, 2),
            0x2567 => (2, 2, 1, 0), 0x2568 => (1, 1, 2, 0), 0x2569 => (2, 2, 2, 0),
            0x256A => (2, 2, 1, 1), 0x256B => (1, 1, 2, 2), 0x256C => (2, 2, 2, 2),
            _ => (0, 0, 0, 0),
        };
        double g = Math.Max(t, Math.Round(w / 5)); // gap between double strokes
        void HLine(double x0, double x1, double yy) => dc.DrawRectangle(b, null, new Rect(Math.Min(x0, x1), yy - t / 2, Math.Abs(x1 - x0), t));
        void VLine(double y0, double y1, double xx) => dc.DrawRectangle(b, null, new Rect(xx - t / 2, Math.Min(y0, y1), t, Math.Abs(y1 - y0)));
        bool vDouble = a.u == 2 || a.d == 2, hDouble = a.l == 2 || a.r == 2;
        // Horizontal arms
        if (a.l == 1) HLine(x, cx + (vDouble ? g : 0), cy);
        if (a.r == 1) HLine(cx - (vDouble ? g : 0), x + w, cy);
        if (a.l == 2) { HLine(x, cx + (a.u == 2 ? -g : g), cy - g); HLine(x, cx + (a.d == 2 ? -g : g), cy + g); }
        if (a.r == 2) { HLine(cx + (a.u == 2 ? g : -g), x + w, cy - g); HLine(cx + (a.d == 2 ? g : -g), x + w, cy + g); }
        // Vertical arms
        if (a.u == 1) VLine(y, cy + (hDouble ? g : 0), cx);
        if (a.d == 1) VLine(cy - (hDouble ? g : 0), y + h, cx);
        if (a.u == 2) { VLine(y, cy + (a.l == 2 ? -g : g), cx - g); VLine(y, cy + (a.r == 2 ? -g : g), cx + g); }
        if (a.d == 2) { VLine(cy + (a.l == 2 ? g : -g), y + h, cx - g); VLine(cy + (a.r == 2 ? g : -g), y + h, cx + g); }
        return true;
    }
}
