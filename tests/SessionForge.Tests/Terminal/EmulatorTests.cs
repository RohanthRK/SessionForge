using System.Text;
using SessionForge.Terminal;

namespace SessionForge.Tests.Terminal;

public class EmulatorTests
{
    private static TerminalEmulator Make(int cols = 20, int rows = 5, int scrollback = 100) => new(cols, rows, scrollback);

    private static string Row(TerminalEmulator t, int y) => t.GetScreenLine(y).GetText();

    [Fact]
    public void PrintsTextAndMovesCursor()
    {
        var t = Make();
        t.Feed("hello");
        Assert.Equal("hello", Row(t, 0));
        Assert.Equal(5, t.CursorX);
        Assert.Equal(0, t.CursorY);
    }

    [Fact]
    public void CrLfMovesToNextLine()
    {
        var t = Make();
        t.Feed("ab\r\ncd");
        Assert.Equal("ab", Row(t, 0));
        Assert.Equal("cd", Row(t, 1));
    }

    [Fact]
    public void Utf8SplitAcrossFeedsIsDecoded()
    {
        var t = Make();
        var bytes = Encoding.UTF8.GetBytes("é€");
        t.Feed(bytes.AsSpan(0, 1));
        t.Feed(bytes.AsSpan(1));
        Assert.Equal("é€", Row(t, 0));
    }

    [Fact]
    public void AutowrapUsesPendingWrapAndMarksSoftWrap()
    {
        var t = Make(cols: 5);
        t.Feed("abcde");
        Assert.Equal(4, t.CursorX);       // pending wrap: cursor stays in last column
        Assert.Equal(0, t.CursorY);
        t.Feed("f");
        Assert.Equal("abcde", Row(t, 0));
        Assert.Equal("f", Row(t, 1));
        Assert.True(t.GetScreenLine(0).IsWrapped);
    }

    [Fact]
    public void CrInPendingWrapDoesNotWrap()
    {
        var t = Make(cols: 5);
        t.Feed("abcde\rX");
        Assert.Equal("Xbcde", Row(t, 0));
        Assert.Equal("", Row(t, 1));
    }

    [Fact]
    public void ScrollsIntoScrollback()
    {
        var t = Make(rows: 3);
        t.Feed("1\r\n2\r\n3\r\n4");
        Assert.Equal(1, t.ScrollbackCount);
        Assert.Equal("1", t.GetLine(0).GetText());
        Assert.Equal("2", Row(t, 0));
        Assert.Equal("4", Row(t, 2));
    }

    [Fact]
    public void ScrollRegionDoesNotTouchScrollback()
    {
        var t = Make(rows: 5);
        t.Feed("A\r\nB\r\nC\r\nD\r\nE");
        t.Feed("\x1b[2;4r");          // region rows 2..4
        t.Feed("\x1b[4;1H\n");         // LF at bottom of region scrolls region
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal("A", Row(t, 0));
        Assert.Equal("C", Row(t, 1));
        Assert.Equal("D", Row(t, 2));
        Assert.Equal("", Row(t, 3));
        Assert.Equal("E", Row(t, 4));
    }

    [Fact]
    public void CursorPositionAndErase()
    {
        var t = Make();
        t.Feed("0123456789");
        t.Feed("\x1b[1;4H\x1b[K");
        Assert.Equal("012", Row(t, 0));
        t.Feed("\x1b[2J");
        Assert.Equal("", Row(t, 0));
    }

    [Fact]
    public void SgrBasicAndTrueColor()
    {
        var t = Make();
        t.Feed("\x1b[1;31mR\x1b[38;2;10;20;30mT\x1b[38:2::1:2:3mU\x1b[0mN");
        var line = t.GetScreenLine(0);
        Assert.True(line[0].Attributes.Has(CellFlags.Bold));
        Assert.Equal(TerminalColor.Indexed(1), line[0].Attributes.Foreground);
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), line[1].Attributes.Foreground);
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), line[2].Attributes.Foreground);
        Assert.Equal(CellAttributes.Default, line[3].Attributes);
    }

    [Fact]
    public void Sgr256AndBright()
    {
        var t = Make();
        t.Feed("\x1b[48;5;196mA\x1b[92mB");
        var line = t.GetScreenLine(0);
        Assert.Equal(TerminalColor.Indexed(196), line[0].Attributes.Background);
        Assert.Equal(TerminalColor.Indexed(10), line[1].Attributes.Foreground);
    }

    [Fact]
    public void EraseUsesBackgroundColor()
    {
        var t = Make();
        t.Feed("\x1b[44m\x1b[2K");
        Assert.Equal(TerminalColor.Indexed(4), t.GetScreenLine(0)[5].Attributes.Background);
    }

    [Fact]
    public void WideCharactersOccupyTwoCells()
    {
        var t = Make();
        t.Feed("a中b");
        var line = t.GetScreenLine(0);
        Assert.Equal(2, line[1].Width);
        Assert.Equal(0, line[2].Width);
        Assert.Equal('b', line[3].Rune);
        Assert.Equal("a中b", Row(t, 0));
        Assert.Equal(4, t.CursorX);
    }

    [Fact]
    public void WideCharAtLastColumnWraps()
    {
        var t = Make(cols: 4);
        t.Feed("abc中");
        Assert.Equal("abc", Row(t, 0));
        Assert.Equal("中", Row(t, 1));
    }

    [Fact]
    public void CombiningCharacterAttachesToPreviousCell()
    {
        var t = Make();
        t.Feed("éx");
        Assert.Equal("éx", Row(t, 0));
        Assert.Equal(2, t.CursorX);
    }

    [Fact]
    public void AlternateScreenPreservesMain()
    {
        var t = Make();
        t.Feed("main");
        t.Feed("\x1b[?1049h");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal("", Row(t, 0));
        t.Feed("alt");
        t.Feed("\x1b[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("main", Row(t, 0));
        Assert.Equal(4, t.CursorX);
    }

    [Fact]
    public void InsertAndDeleteCharacters()
    {
        var t = Make();
        t.Feed("abcdef\x1b[1;3H\x1b[2@");
        Assert.Equal("ab  cdef", Row(t, 0));
        t.Feed("\x1b[2P");
        Assert.Equal("abcdef", Row(t, 0));
    }

    [Fact]
    public void InsertAndDeleteLines()
    {
        var t = Make(rows: 4);
        t.Feed("1\r\n2\r\n3\r\n4\x1b[2;1H\x1b[L");
        Assert.Equal(new[] { "1", "", "2", "3" }, Enumerable.Range(0, 4).Select(y => Row(t, y)));
        t.Feed("\x1b[M");
        Assert.Equal(new[] { "1", "2", "3", "" }, Enumerable.Range(0, 4).Select(y => Row(t, y)));
    }

    [Fact]
    public void DecSpecialGraphicsLineDrawing()
    {
        var t = Make();
        t.Feed("\x1b(0lqk\x1b(B");
        Assert.Equal("┌─┐", Row(t, 0));
    }

    [Fact]
    public void ReportsCursorPositionAndDeviceAttributes()
    {
        var t = Make();
        var replies = new List<string>();
        t.Response += b => replies.Add(Encoding.UTF8.GetString(b));
        t.Feed("\x1b[3;5H\x1b[6n\x1b[c");
        Assert.Equal("\x1b[3;5R", replies[0]);
        Assert.StartsWith("\x1b[?", replies[1]);
    }

    [Fact]
    public void OscSetsTitleWithBelOrSt()
    {
        var t = Make();
        string? title = null;
        t.TitleChanged += s => title = s;
        t.Feed("\x1b]0;hello\x07");
        Assert.Equal("hello", title);
        t.Feed("\x1b]2;world\x1b\\");
        Assert.Equal("world", title);
    }

    [Fact]
    public void Osc7ReportsWorkingDirectory()
    {
        var t = Make();
        string? cwd = null;
        t.WorkingDirectoryChanged += s => cwd = s;
        t.Feed("\x1b]7;file://host/home/user/my%20dir\x07");
        Assert.Equal("/home/user/my dir", cwd);
    }

    [Fact]
    public void Osc52IsBlockedByDefault()
    {
        var t = Make();
        string? clip = null;
        t.ClipboardWriteRequested += s => clip = s;
        t.Feed("\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("secret")) + "\x07");
        Assert.Null(clip);
        t.AllowClipboardWrite = true;
        t.Feed("\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("ok")) + "\x07");
        Assert.Equal("ok", clip);
    }

    [Fact]
    public void Osc8HyperlinkIsAttachedToCells()
    {
        var t = Make();
        t.Feed("\x1b]8;;https://example.com\x1b\\link\x1b]8;;\x1b\\ plain");
        var line = t.GetScreenLine(0);
        Assert.Equal("https://example.com", t.GetHyperlink(line[0].Attributes.LinkId));
        Assert.Null(t.GetHyperlink(line[6].Attributes.LinkId));
    }

    [Fact]
    public void ModesAreTracked()
    {
        var t = Make();
        t.Feed("\x1b[?1h\x1b[?2004h\x1b[?1006h\x1b[?1002h\x1b[?25l\x1b[5 q");
        Assert.True(t.Modes.ApplicationCursorKeys);
        Assert.True(t.Modes.BracketedPaste);
        Assert.Equal(MouseEncoding.Sgr, t.Modes.MouseEncoding);
        Assert.Equal(MouseTrackingMode.ButtonEvent, t.Modes.MouseTracking);
        Assert.False(t.Modes.CursorVisible);
        Assert.Equal(CursorShape.Bar, t.Modes.CursorShape);
    }

    [Fact]
    public void TabStops()
    {
        var t = Make(cols: 30);
        t.Feed("a\tb");
        Assert.Equal(9, t.CursorX);
        t.Feed("\x1b[3g\r\tc");
        Assert.Equal(29, t.CursorX); // no stops: goes to last column
    }

    [Fact]
    public void RepeatPrecedingCharacter()
    {
        var t = Make();
        t.Feed("x\x1b[3b");
        Assert.Equal("xxxx", Row(t, 0));
    }

    [Fact]
    public void ResizeReflowsSoftWrappedLines()
    {
        var t = Make(cols: 10, rows: 4);
        t.Feed("0123456789ABCDE");    // wraps at 10
        Assert.Equal("0123456789", Row(t, 0));
        Assert.Equal("ABCDE", Row(t, 1));
        t.Resize(20, 4);
        Assert.Equal("0123456789ABCDE", Row(t, 0));
        Assert.Equal(15, t.CursorX);
        Assert.Equal(0, t.CursorY);
        t.Resize(5, 4);
        Assert.Equal(new[] { "01234", "56789", "ABCDE" }, Enumerable.Range(0, 3).Select(y => Row(t, y)));
        // Cursor was at logical offset 15, which at width 5 is the start of the 4th row.
        Assert.Equal(3, t.CursorY);
        Assert.Equal(0, t.CursorX);
    }

    [Fact]
    public void ResizeKeepsHardLineBreaks()
    {
        var t = Make(cols: 10, rows: 4);
        t.Feed("ab\r\ncd");
        t.Resize(4, 4);
        Assert.Equal("ab", Row(t, 0));
        Assert.Equal("cd", Row(t, 1));
        Assert.Equal(1, t.CursorY);
        Assert.Equal(2, t.CursorX);
    }

    [Fact]
    public void ShrinkingRowsPushesTopIntoScrollback()
    {
        var t = Make(cols: 10, rows: 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        t.Resize(10, 3);
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("3", Row(t, 0));
        Assert.Equal("5", Row(t, 2));
        Assert.Equal(2, t.CursorY);
        t.Resize(10, 5);
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal("1", Row(t, 0));
        Assert.Equal(4, t.CursorY);
    }

    [Fact]
    public void GetTextJoinsWrappedLines()
    {
        var t = Make(cols: 5);
        t.Feed("abcdefg\r\nxy");
        var text = t.GetText(new BufferPoint(0, 0), new BufferPoint(2, 4));
        Assert.Equal("abcdefg\r\nxy", text);
    }

    [Fact]
    public void FindAcrossScrollbackAndWraps()
    {
        var t = Make(cols: 5, rows: 2);
        t.Feed("hello world\r\nfoo\r\nbar");
        var r = t.Find("lo wo", new BufferPoint(-1, 0), new SearchOptions());
        Assert.NotNull(r);
        Assert.Equal(new BufferPoint(0, 3), r!.Value.Start);
        var back = t.Find("FOO", new BufferPoint(t.TotalLines, 0), new SearchOptions(Backward: true));
        Assert.NotNull(back);
        Assert.Null(t.Find("FOO", new BufferPoint(-1, 0), new SearchOptions(MatchCase: true)));
        Assert.NotNull(t.Find("b.r", new BufferPoint(-1, 0), new SearchOptions(Regex: true)));
    }

    [Fact]
    public void WordSelection()
    {
        var t = Make(cols: 30);
        t.Feed("ls -la /var/log/syslog now");
        var w = t.GetWordAt(new BufferPoint(0, 10));
        Assert.Equal("/var/log/syslog", t.GetText(w.Start, w.End));
    }

    [Fact]
    public void ScrollbackCapacityEvictsOldest()
    {
        var t = Make(rows: 2, scrollback: 3);
        for (int i = 0; i < 10; i++) t.Feed($"{i}\r\n");
        Assert.Equal(3, t.ScrollbackCount);
        Assert.Equal(6, t.FirstLineId);
        Assert.Equal("6", t.GetLine(0).GetText());
    }

    [Fact]
    public void OriginModeRelativeAddressing()
    {
        var t = Make(rows: 6);
        t.Feed("\x1b[3;5r\x1b[?6h\x1b[1;1HX");
        Assert.Equal("X", Row(t, 2));
        t.Feed("\x1b[10;1HY"); // clamped to region bottom
        Assert.Equal("Y", Row(t, 4));
    }

    [Fact]
    public void SaveRestoreCursorRestoresAttributes()
    {
        var t = Make();
        // Note: "\u001b7" not "\x1b7" — C# \x consumes up to 4 hex digits.
        t.Feed("\x1b[2;3H\x1b[31m\u001b7\x1b[0m\x1b[5;5H\u001b8A");
        Assert.Equal('A', t.GetScreenLine(1)[2].Rune);
        Assert.Equal(TerminalColor.Indexed(1), t.GetScreenLine(1)[2].Attributes.Foreground);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var t = Make();
        t.Feed("abc\x1b[?1h\u001bc");
        Assert.Equal("", Row(t, 0));
        Assert.False(t.Modes.ApplicationCursorKeys);
    }

    [Fact]
    public void GarbageInputNeverThrows()
    {
        var t = Make(cols: 13, rows: 7);
        var rnd = new Random(1234);
        var buf = new byte[4096];
        for (int i = 0; i < 300; i++)
        {
            rnd.NextBytes(buf);
            // bias towards escape sequences
            for (int k = 0; k < buf.Length; k += 7) buf[k] = (byte)"\x1b[;?0123456789mHJK\x07]P\\"[rnd.Next(21)];
            t.Feed(buf);
            if (i % 50 == 0) t.Resize(5 + rnd.Next(30), 2 + rnd.Next(10));
        }
        Assert.InRange(t.CursorX, 0, t.Columns - 1);
        Assert.InRange(t.CursorY, 0, t.Rows - 1);
    }
}
