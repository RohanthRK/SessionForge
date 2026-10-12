namespace SessionForge.Terminal;

public enum ColorKind : byte { Default = 0, Indexed = 1, Rgb = 2 }

/// <summary>A terminal colour: default, palette index (0-255) or 24-bit RGB. Packed into 4 bytes.</summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private readonly uint _v;
    private TerminalColor(uint v) => _v = v;

    public static readonly TerminalColor Default = default;
    public static TerminalColor Indexed(int index) => new((1u << 24) | (uint)(index & 0xFF));
    public static TerminalColor Rgb(byte r, byte g, byte b) => new((2u << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    public ColorKind Kind => (ColorKind)(_v >> 24);
    public int Index => (int)(_v & 0xFF);
    public byte R => (byte)(_v >> 16);
    public byte G => (byte)(_v >> 8);
    public byte B => (byte)_v;
    public bool IsDefault => _v == 0;

    public bool Equals(TerminalColor other) => _v == other._v;
    public override bool Equals(object? obj) => obj is TerminalColor c && c._v == _v;
    public override int GetHashCode() => (int)_v;
    public static bool operator ==(TerminalColor a, TerminalColor b) => a._v == b._v;
    public static bool operator !=(TerminalColor a, TerminalColor b) => a._v != b._v;
    public override string ToString() => Kind switch
    {
        ColorKind.Indexed => $"idx{Index}",
        ColorKind.Rgb => $"#{R:X2}{G:X2}{B:X2}",
        _ => "default",
    };
}

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    DoubleUnderline = 1 << 4,
    Blink = 1 << 5,
    Inverse = 1 << 6,
    Hidden = 1 << 7,
    Strikethrough = 1 << 8,
    Overline = 1 << 9,
    CurlyUnderline = 1 << 10,
}

/// <summary>Visual attributes of a cell (SGR state).</summary>
public readonly record struct CellAttributes(TerminalColor Foreground, TerminalColor Background, CellFlags Flags, int LinkId = 0)
{
    public static readonly CellAttributes Default = default;
    public bool Has(CellFlags f) => (Flags & f) != 0;
    /// <summary>Attributes used for erased cells: background colour only (BCE).</summary>
    public CellAttributes ForErase() => new(TerminalColor.Default, Background, CellFlags.None);
}

/// <summary>
/// One terminal cell. <see cref="Rune"/> 0 means blank. A wide character occupies two cells:
/// the lead cell has Width 2 and the following cell has Width 0.
/// </summary>
public struct Cell
{
    public int Rune;
    public byte Width;
    public CellAttributes Attributes;

    public static Cell Blank(in CellAttributes attr) => new() { Rune = 0, Width = 1, Attributes = attr };

    public readonly bool IsBlank => Rune == 0 || Rune == ' ';
    public readonly bool IsWideTrailer => Width == 0;
}
