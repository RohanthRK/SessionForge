using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SessionForge.Terminal;
using TMouseButton = SessionForge.Terminal.MouseButton;
using MouseAction = SessionForge.Terminal.MouseAction;

namespace SessionForge.App.Controls;

public sealed partial class TerminalControl
{
    private bool _selecting;
    private DispatcherTimer? _autoScrollTimer;
    private Point _lastMouse;
    private int _mouseButtonsDown;
    private (int Col, int Row) _lastReportedCell = (-1, -1);

    /// <summary>Application shortcuts the host window wants to handle (return true if handled).</summary>
    public Func<KeyEventArgs, bool>? ShortcutHandler { get; set; }

    private void Send(string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (!_follow) ScrollToBottom();
        ClearSelectionOnInput();
        ResetBlink();
        SendInput?.Invoke(s);
    }

    private void ClearSelectionOnInput()
    {
        if (_selAnchor != null && !_selecting) ClearSelection();
    }

    private TerminalModes Modes => _emulator?.Modes ?? new TerminalModes();

    #region Keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _emulator == null) return;
        if (ShortcutHandler?.Invoke(e) == true) { e.Handled = true; return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        var wm = Keyboard.Modifiers;
        bool ctrl = (wm & ModifierKeys.Control) != 0, alt = (wm & ModifierKeys.Alt) != 0, shift = (wm & ModifierKeys.Shift) != 0;
        bool altGr = ctrl && alt && Keyboard.IsKeyDown(Key.RightAlt);
        if (alt && !ctrl && key == Key.F4) return; // keep Alt+F4 for closing the window

        // Built-in clipboard and scrolling shortcuts (work regardless of host shortcuts).
        if (ctrl && shift && key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.V) { Paste(); e.Handled = true; return; }
        if (shift && !ctrl && key == Key.Insert) { Paste(); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.Insert) { CopySelection(); e.Handled = true; return; }
        if (shift && !ctrl && key == Key.PageUp) { ScrollBy(-Math.Max(1, Rows - 1)); e.Handled = true; return; }
        if (shift && !ctrl && key == Key.PageDown) { ScrollBy(Math.Max(1, Rows - 1)); e.Handled = true; return; }
        if (ctrl && shift && key == Key.Home) { ScrollTo(0); e.Handled = true; return; }
        if (ctrl && shift && key == Key.End) { ScrollToBottom(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.Up) { ScrollBy(-1); e.Handled = true; return; }
        if (ctrl && shift && key == Key.Down) { ScrollBy(1); e.Handled = true; return; }
        if (ctrl && !shift && (key == Key.OemPlus || key == Key.Add)) { Zoom(1); e.Handled = true; return; }
        if (ctrl && !shift && (key == Key.OemMinus || key == Key.Subtract)) { Zoom(-1); e.Handled = true; return; }

        var mods = (shift ? KeyModifiers.Shift : 0) | (alt && !altGr ? KeyModifiers.Alt : 0) | (ctrl && !altGr ? KeyModifiers.Control : 0);
        var tkey = MapKey(key);
        if (tkey != TerminalKey.None)
        {
            Send(InputEncoder.EncodeKey(tkey, mods, Modes, KeyboardOptions));
            e.Handled = true;
            return;
        }

        if (altGr) return; // AltGr combinations arrive as text input

        // Ctrl / Alt + character keys: translate the key to its character on the current layout.
        if ((ctrl || alt) && !IsModifierKey(key))
        {
            var ch = CharFromKey(key, shift && !ctrl);
            if (ch != '\0')
            {
                var s = InputEncoder.EncodeChar(ch, mods & ~KeyModifiers.Shift, KeyboardOptions);
                if (s != null) { Send(s); e.Handled = true; }
            }
            else if (alt && !ctrl)
            {
                e.Handled = true; // swallow so the window menu doesn't steal focus
            }
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        // Control characters were already handled in key-down.
        var sb = new StringBuilder(e.Text.Length);
        foreach (var c in e.Text) if (c >= ' ' && c != '\x7f') sb.Append(c);
        if (sb.Length == 0) return;
        Send(sb.ToString());
        e.Handled = true;
    }

    private static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static TerminalKey MapKey(Key k) => k switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down, Key.Left => TerminalKey.Left, Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home, Key.End => TerminalKey.End, Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
        Key.F1 => TerminalKey.F1, Key.F2 => TerminalKey.F2, Key.F3 => TerminalKey.F3, Key.F4 => TerminalKey.F4,
        Key.F5 => TerminalKey.F5, Key.F6 => TerminalKey.F6, Key.F7 => TerminalKey.F7, Key.F8 => TerminalKey.F8,
        Key.F9 => TerminalKey.F9, Key.F10 => TerminalKey.F10, Key.F11 => TerminalKey.F11, Key.F12 => TerminalKey.F12,
        _ => TerminalKey.None,
    };

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>Character produced by a key on the active layout, ignoring Ctrl/Alt (optionally with Shift).</summary>
    private static char CharFromKey(Key key, bool shift)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        var hkl = GetKeyboardLayout(0);
        var state = new byte[256];
        if (shift) state[0x10] = 0x80;
        var sb = new StringBuilder(4);
        uint scan = MapVirtualKeyEx((uint)vk, 0, hkl);
        // wFlags=4: don't change keyboard state (avoids breaking dead keys) on Windows 10 1607+.
        int n = ToUnicodeEx((uint)vk, scan, state, sb, sb.Capacity, 4, hkl);
        return n == 1 ? sb[0] : '\0';
    }

    #endregion

    #region Paste

    public void Paste()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (COMException) { return; }
        PasteText(text);
    }

    /// <summary>Optional custom delivery of (already encoded) paste data, e.g. line-by-line with delays.</summary>
    public Action<string>? PasteSink { get; set; }

    public void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (ConfirmPaste != null)
        {
            var confirmed = ConfirmPaste(text);
            if (confirmed == null) return;
            text = confirmed;
        }
        var encoded = InputEncoder.EncodePaste(text, Modes);
        if (PasteSink != null && !Modes.BracketedPaste)
        {
            if (!_follow) ScrollToBottom();
            PasteSink(encoded);
        }
        else Send(encoded);
    }

    #endregion

    #region Mouse

    private bool MouseReportingActive => Modes.MouseTracking != MouseTrackingMode.None && (Keyboard.Modifiers & ModifierKeys.Shift) == 0;

    private static KeyModifiers CurrentMods()
    {
        var m = Keyboard.Modifiers;
        return ((m & ModifierKeys.Shift) != 0 ? KeyModifiers.Shift : 0) | ((m & ModifierKeys.Alt) != 0 ? KeyModifiers.Alt : 0) | ((m & ModifierKeys.Control) != 0 ? KeyModifiers.Control : 0);
    }

    private static TMouseButton Map(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Left => TMouseButton.Left,
        System.Windows.Input.MouseButton.Middle => TMouseButton.Middle,
        System.Windows.Input.MouseButton.Right => TMouseButton.Right,
        _ => TMouseButton.None,
    };

    private bool Report(TMouseButton b, MouseAction action, Point p)
    {
        if (_fonts == null) return false;
        var (col, row) = GridCell(p);
        if (action == MouseAction.Move)
        {
            if ((col, row) == _lastReportedCell) return true;
            _lastReportedCell = (col, row);
        }
        var s = MouseEncoder.Encode(b, action, col, row, CurrentMods(), Modes, _mouseButtonsDown > 0);
        if (s == null) return false;
        SendInput?.Invoke(s);
        return true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var p = e.GetPosition(this);

        if (e.ChangedButton == System.Windows.Input.MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && LinkAt(p) is { } link)
        {
            OpenLink?.Invoke(link);
            e.Handled = true;
            return;
        }

        if (MouseReportingActive && e.ChangedButton != System.Windows.Input.MouseButton.XButton1 && e.ChangedButton != System.Windows.Input.MouseButton.XButton2)
        {
            _mouseButtonsDown++;
            if (Report(Map(e.ChangedButton), MouseAction.Press, p)) { CaptureMouse(); e.Handled = true; return; }
        }

        switch (e.ChangedButton)
        {
            case System.Windows.Input.MouseButton.Left:
                if (_emulator == null) return;
                var bp = HitTestCell(p);
                bool block = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                if (e.ClickCount == 2)
                {
                    var w = _emulator.GetWordAt(bp, WordDelimiters);
                    SetSelection(w.Start, w.End, false);
                    FinishSelection();
                }
                else if (e.ClickCount >= 3)
                {
                    var l = _emulator.GetLogicalLineAt(bp.Line);
                    SetSelection(l.Start, l.End, false);
                    FinishSelection();
                }
                else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selAnchor != null)
                {
                    ExtendSelection(bp);
                    _selecting = true;
                    CaptureMouse();
                }
                else
                {
                    ClearSelection();
                    SetSelection(bp, bp, block);
                    _selecting = true;
                    CaptureMouse();
                }
                e.Handled = true;
                break;
            case System.Windows.Input.MouseButton.Middle:
                Paste();
                e.Handled = true;
                break;
            case System.Windows.Input.MouseButton.Right:
                bool menuForced = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
                if (RightClickPastes && !menuForced)
                {
                    if (HasSelection && !CopyOnSelect) { CopySelection(); ClearSelection(); }
                    else Paste();
                }
                else
                {
                    var menu = ContextMenuFactory?.Invoke();
                    if (menu != null)
                    {
                        menu.PlacementTarget = this;
                        menu.IsOpen = true;
                    }
                }
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _lastMouse = p;
        if (MouseReportingActive && (Modes.MouseTracking is MouseTrackingMode.AnyEvent || (Modes.MouseTracking is MouseTrackingMode.ButtonEvent && _mouseButtonsDown > 0)))
        {
            var btn = e.LeftButton == MouseButtonState.Pressed ? TMouseButton.Left : e.MiddleButton == MouseButtonState.Pressed ? TMouseButton.Middle :
                      e.RightButton == MouseButtonState.Pressed ? TMouseButton.Right : TMouseButton.None;
            Report(btn, MouseAction.Move, p);
            return;
        }
        if (_selecting)
        {
            ExtendSelection(HitTestCell(p));
            UpdateAutoScroll(p);
        }
        else
        {
            Cursor = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && LinkAt(p) != null ? Cursors.Hand : Cursors.IBeam;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        if (_mouseButtonsDown > 0)
        {
            _mouseButtonsDown = Math.Max(0, _mouseButtonsDown - 1);
            if (MouseReportingActive) Report(Map(e.ChangedButton), MouseAction.Release, p);
            if (_mouseButtonsDown == 0 && IsMouseCaptured && !_selecting) ReleaseMouseCapture();
            e.Handled = true;
            if (!_selecting) return;
        }
        if (_selecting && e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            _selecting = false;
            StopAutoScroll();
            ReleaseMouseCapture();
            FinishSelection();
            e.Handled = true;
        }
    }

    private void FinishSelection()
    {
        if (HasSelection && CopyOnSelect) CopySelection();
        if (_selAnchor == _selActive) ClearSelection();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Zoom(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        var p = e.GetPosition(this);
        if (MouseReportingActive)
        {
            for (int i = 0; i < notches; i++) Report(e.Delta > 0 ? TMouseButton.WheelUp : TMouseButton.WheelDown, MouseAction.Press, p);
            e.Handled = true;
            return;
        }
        if (_emulator?.IsAlternateScreen == true && Modes.AlternateScroll)
        {
            var seq = InputEncoder.EncodeKey(e.Delta > 0 ? TerminalKey.Up : TerminalKey.Down, KeyModifiers.None, Modes);
            for (int i = 0; i < notches * 3; i++) SendInput?.Invoke(seq!);
            e.Handled = true;
            return;
        }
        ScrollBy((e.Delta > 0 ? -3 : 3) * notches);
        e.Handled = true;
    }

    private void UpdateAutoScroll(Point p)
    {
        int dir = p.Y < 0 ? -1 : p.Y > ActualHeight ? 1 : 0;
        if (dir == 0) { StopAutoScroll(); return; }
        _autoScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) =>
        {
            int d = _lastMouse.Y < 0 ? -1 : _lastMouse.Y > ActualHeight ? 1 : 0;
            if (d == 0) return;
            ScrollBy(d);
            ExtendSelection(HitTestCell(_lastMouse));
        }, Dispatcher);
        _autoScrollTimer.Start();
    }

    private void StopAutoScroll() => _autoScrollTimer?.Stop();

    #endregion

    #region Focus

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ResetBlink();
        if (Modes.FocusEvents) SendInput?.Invoke(InputEncoder.EncodeFocus(true));
        InvalidateTerminal();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (Modes.FocusEvents) SendInput?.Invoke(InputEncoder.EncodeFocus(false));
        InvalidateTerminal();
    }

    #endregion
}
