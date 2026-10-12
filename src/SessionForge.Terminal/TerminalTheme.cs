namespace SessionForge.Terminal;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new FormatException($"Invalid colour '{hex}'");
        return new Rgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    public override string ToString() => ToHex();
}

/// <summary>Colour scheme: 16 ANSI colours (the 256-colour cube is derived) plus default fg/bg, cursor and selection.</summary>
public sealed class TerminalTheme
{
    public string Name { get; set; } = "Custom";
    public Rgb Foreground { get; set; }
    public Rgb Background { get; set; }
    public Rgb Cursor { get; set; }
    public Rgb SelectionBackground { get; set; }
    public Rgb[] Ansi { get; set; } = new Rgb[16];

    /// <summary>Resolves a 0-255 palette index.</summary>
    public Rgb Palette(int index)
    {
        if (index < 16) return Ansi[index];
        if (index < 232)
        {
            int i = index - 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return new Rgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }
        byte g = (byte)(8 + (index - 232) * 10);
        return new Rgb(g, g, g);
    }

    public TerminalTheme Clone() => new()
    {
        Name = Name, Foreground = Foreground, Background = Background, Cursor = Cursor,
        SelectionBackground = SelectionBackground, Ansi = (Rgb[])Ansi.Clone(),
    };

    private static TerminalTheme Make(string name, string fg, string bg, string cursor, string sel, params string[] ansi) => new()
    {
        Name = name, Foreground = Rgb.FromHex(fg), Background = Rgb.FromHex(bg), Cursor = Rgb.FromHex(cursor),
        SelectionBackground = Rgb.FromHex(sel), Ansi = ansi.Select(Rgb.FromHex).ToArray(),
    };

    public static TerminalTheme ForgeDark() => Make("Forge Dark", "#D4D8DF", "#16181D", "#5EB1FF", "#2F4F7A",
        "#1E2127", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#ABB2BF",
        "#5C6370", "#FF7A85", "#B5E890", "#FFD68A", "#82C4FF", "#DE99F0", "#7FD3DE", "#FFFFFF");

    public static TerminalTheme ForgeLight() => Make("Forge Light", "#24292F", "#FBFBFC", "#0969DA", "#B6D7FF",
        "#24292F", "#CF222E", "#116329", "#7D4E00", "#0550AE", "#8250DF", "#1B7C83", "#6E7781",
        "#57606A", "#A40E26", "#1A7F37", "#953800", "#218BFF", "#A475F9", "#3192AA", "#8C959F");

    public static TerminalTheme Solarized() => Make("Solarized Dark", "#839496", "#002B36", "#93A1A1", "#073642",
        "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
        "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3");

    public static TerminalTheme Classic() => Make("Classic (xterm)", "#E5E5E5", "#000000", "#E5E5E5", "#3A3D41",
        "#000000", "#CD0000", "#00CD00", "#CDCD00", "#0000EE", "#CD00CD", "#00CDCD", "#E5E5E5",
        "#7F7F7F", "#FF0000", "#00FF00", "#FFFF00", "#5C5CFF", "#FF00FF", "#00FFFF", "#FFFFFF");

    public static TerminalTheme Dracula() => Make("Dracula", "#F8F8F2", "#282A36", "#F8F8F2", "#44475A",
        "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
        "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF");

    public static IReadOnlyList<TerminalTheme> BuiltIn { get; } = [ForgeDark(), ForgeLight(), Solarized(), Dracula(), Classic()];
}
