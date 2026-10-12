using SessionForge.Terminal;

namespace SessionForge.Tests.Terminal;

public class InputEncoderTests
{
    private static TerminalModes Modes(string seq = "")
    {
        var t = new TerminalEmulator();
        t.Feed(seq);
        return t.Modes;
    }

    [Fact]
    public void CursorKeysNormalAndApplication()
    {
        Assert.Equal("\x1b[A", InputEncoder.EncodeKey(TerminalKey.Up, KeyModifiers.None, Modes()));
        Assert.Equal("\x1bOA", InputEncoder.EncodeKey(TerminalKey.Up, KeyModifiers.None, Modes("\x1b[?1h")));
        Assert.Equal("\x1b[1;5C", InputEncoder.EncodeKey(TerminalKey.Right, KeyModifiers.Control, Modes()));
    }

    [Fact]
    public void FunctionKeys()
    {
        Assert.Equal("\x1bOP", InputEncoder.EncodeKey(TerminalKey.F1, KeyModifiers.None, Modes()));
        Assert.Equal("\x1b[15~", InputEncoder.EncodeKey(TerminalKey.F5, KeyModifiers.None, Modes()));
        Assert.Equal("\x1b[24;2~", InputEncoder.EncodeKey(TerminalKey.F12, KeyModifiers.Shift, Modes()));
    }

    [Fact]
    public void BackspaceOptions()
    {
        Assert.Equal("\x7f", InputEncoder.EncodeKey(TerminalKey.Backspace, KeyModifiers.None, Modes()));
        Assert.Equal("\b", InputEncoder.EncodeKey(TerminalKey.Backspace, KeyModifiers.None, Modes(), new KeyboardOptions { BackspaceSendsControlH = true }));
    }

    [Fact]
    public void ControlCharacters()
    {
        Assert.Equal("\x03", InputEncoder.EncodeChar('c', KeyModifiers.Control));
        Assert.Equal("\0", InputEncoder.EncodeChar(' ', KeyModifiers.Control));
        Assert.Equal("\x1bx", InputEncoder.EncodeChar('x', KeyModifiers.Alt));
    }

    [Fact]
    public void BracketedPasteWrapsAndSanitises()
    {
        var m = Modes("\x1b[?2004h");
        Assert.Equal("\x1b[200~a\rb\x1b[201~", InputEncoder.EncodePaste("a\r\nb", m));
        Assert.Equal("\x1b[200~xy\x1b[201~", InputEncoder.EncodePaste("x\x1b[201~y", m));
        Assert.Equal("a\rb", InputEncoder.EncodePaste("a\nb", Modes()));
    }

    [Fact]
    public void SgrMouse()
    {
        var m = Modes("\x1b[?1000h\x1b[?1006h");
        Assert.Equal("\x1b[<0;3;2M", MouseEncoder.Encode(MouseButton.Left, MouseAction.Press, 2, 1, KeyModifiers.None, m, false));
        Assert.Equal("\x1b[<0;3;2m", MouseEncoder.Encode(MouseButton.Left, MouseAction.Release, 2, 1, KeyModifiers.None, m, false));
        Assert.Null(MouseEncoder.Encode(MouseButton.None, MouseAction.Move, 2, 1, KeyModifiers.None, m, false));
        Assert.Equal("\x1b[<64;1;1M", MouseEncoder.Encode(MouseButton.WheelUp, MouseAction.Press, 0, 0, KeyModifiers.None, m, false));
    }

    [Fact]
    public void X10MouseEncoding()
    {
        var m = Modes("\x1b[?1000h");
        Assert.Equal("\x1b[M !!", MouseEncoder.Encode(MouseButton.Left, MouseAction.Press, 0, 0, KeyModifiers.None, m, false));
        Assert.Null(MouseEncoder.Encode(MouseButton.Left, MouseAction.Press, 300, 0, KeyModifiers.None, m, false));
    }

    [Fact]
    public void NoMouseReportsWhenTrackingOff()
    {
        Assert.Null(MouseEncoder.Encode(MouseButton.Left, MouseAction.Press, 0, 0, KeyModifiers.None, Modes(), false));
    }
}
