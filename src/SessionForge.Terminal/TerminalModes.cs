namespace SessionForge.Terminal;

public enum MouseTrackingMode { None, X10, Normal, ButtonEvent, AnyEvent }
public enum MouseEncoding { Default, Utf8, Sgr, Urxvt }
public enum CursorShape { Block, Underline, Bar }

/// <summary>Snapshot-able mode flags that input encoders and the renderer need.</summary>
public sealed class TerminalModes
{
    public bool ApplicationCursorKeys { get; internal set; }
    public bool ApplicationKeypad { get; internal set; }
    public bool AutoWrap { get; internal set; } = true;
    public bool OriginMode { get; internal set; }
    public bool InsertMode { get; internal set; }
    public bool LineFeedNewLine { get; internal set; }
    public bool CursorVisible { get; internal set; } = true;
    public bool CursorBlink { get; internal set; } = true;
    public CursorShape CursorShape { get; internal set; } = CursorShape.Block;
    public bool ReverseVideo { get; internal set; }
    public bool BracketedPaste { get; internal set; }
    public bool FocusEvents { get; internal set; }
    public bool AlternateScroll { get; internal set; } = true;
    public bool SynchronizedOutput { get; internal set; }
    public MouseTrackingMode MouseTracking { get; internal set; }
    public MouseEncoding MouseEncoding { get; internal set; }

    internal void Reset()
    {
        ApplicationCursorKeys = ApplicationKeypad = OriginMode = InsertMode = LineFeedNewLine = false;
        ReverseVideo = BracketedPaste = FocusEvents = SynchronizedOutput = false;
        AutoWrap = CursorVisible = CursorBlink = AlternateScroll = true;
        CursorShape = CursorShape.Block;
        MouseTracking = MouseTrackingMode.None;
        MouseEncoding = MouseEncoding.Default;
    }
}
