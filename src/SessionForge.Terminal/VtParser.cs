using System.Text;

namespace SessionForge.Terminal;

/// <summary>Receives parsed actions from <see cref="VtParser"/>.</summary>
public interface IVtHandler
{
    void Print(int codePoint);
    void Execute(int control);
    void CsiDispatch(CsiParams p, char privateMarker, string intermediates, char final);
    void EscDispatch(string intermediates, char final);
    void OscDispatch(string data);
    void DcsDispatch(char privateMarker, CsiParams p, string intermediates, char final, string data);
}

/// <summary>
/// CSI parameter list. Values of 0 also represent omitted parameters (VT convention).
/// Colon-separated sub-parameters (e.g. <c>38:2::255:0:0</c>) are flagged via <see cref="IsSub"/>.
/// </summary>
public sealed class CsiParams
{
    public const int Max = 32;
    private readonly int[] _values = new int[Max];
    private readonly bool[] _sub = new bool[Max];
    private readonly bool[] _present = new bool[Max];

    public int Count { get; private set; }

    public int this[int i] => i < Count ? _values[i] : 0;
    public bool IsSub(int i) => i < Count && _sub[i];
    public bool IsPresent(int i) => i < Count && _present[i];

    /// <summary>Parameter value, or <paramref name="def"/> when omitted or zero.</summary>
    public int Get(int i, int def) { int v = this[i]; return v == 0 ? def : v; }

    internal void Reset() { Count = 0; _values[0] = 0; _present[0] = false; _sub[0] = false; }

    internal void Digit(int d)
    {
        if (Count == 0) { Count = 1; _values[0] = 0; _sub[0] = false; _present[0] = false; }
        ref int v = ref _values[Count - 1];
        if (v < 100_000_000) v = v * 10 + d;
        _present[Count - 1] = true;
    }

    internal void Next(bool sub)
    {
        if (Count == 0) { Count = 1; _values[0] = 0; _sub[0] = false; _present[0] = false; }
        if (Count >= Max) return;
        _values[Count] = 0;
        _sub[Count] = sub;
        _present[Count] = false;
        Count++;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Count; i++)
        {
            if (i > 0) sb.Append(_sub[i] ? ':' : ';');
            if (_present[i]) sb.Append(_values[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// DEC ANSI-compatible escape sequence parser (after P. Williams' VT500 state machine),
/// operating on Unicode scalars. Byte decoding happens before this stage.
/// </summary>
public sealed class VtParser
{
    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, CsiIgnore, OscString, DcsEntry, DcsParam, DcsIntermediate, DcsPassthrough, DcsIgnore, SosPmApc }

    private const int MaxStringLength = 1 << 20;

    private readonly IVtHandler _h;
    private readonly CsiParams _params = new();
    private readonly StringBuilder _intermediates = new(4);
    private readonly StringBuilder _str = new();
    private State _state = State.Ground;
    private char _private;
    private bool _escInString; // saw ESC inside OSC/DCS/SOS: expecting '\'
    private char _dcsFinal;

    public VtParser(IVtHandler handler) => _h = handler;

    public void Reset()
    {
        _state = State.Ground;
        Clear();
    }

    private void Clear()
    {
        _params.Reset();
        _intermediates.Clear();
        _private = '\0';
    }

    public void Feed(ReadOnlySpan<char> chars)
    {
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            int cp = c;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) { cp = char.ConvertToUtf32(c, chars[i + 1]); i++; }
                else cp = 0xFFFD;
            }
            else if (char.IsLowSurrogate(c)) cp = 0xFFFD;
            Advance(cp);
        }
    }

    public void Advance(int cp)
    {
        // String states consume nearly everything.
        switch (_state)
        {
            case State.OscString: OscChar(cp); return;
            case State.DcsPassthrough:
            case State.DcsIgnore:
            case State.SosPmApc: StringChar(cp); return;
        }

        // "Anywhere" transitions.
        if (cp == 0x1B) { _state = State.Escape; Clear(); return; }
        if (cp == 0x18 || cp == 0x1A) { _state = State.Ground; _h.Execute(cp); return; }

        if (cp < 0x20)
        {
            if (_state is State.CsiEntry or State.CsiParam or State.CsiIntermediate or State.CsiIgnore or State.Escape or State.EscapeIntermediate or State.Ground)
                _h.Execute(cp);
            return; // DCS entry/param states ignore C0
        }

        switch (_state)
        {
            case State.Ground:
                if (cp == 0x7F) return;
                _h.Print(cp);
                return;

            case State.Escape:
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); _state = State.EscapeIntermediate; return; }
                switch (cp)
                {
                    case '[': Clear(); _state = State.CsiEntry; return;
                    case ']': _str.Clear(); _escInString = false; _state = State.OscString; return;
                    case 'P': Clear(); _state = State.DcsEntry; return;
                    case 'X': case '^': case '_': _str.Clear(); _escInString = false; _state = State.SosPmApc; return;
                }
                if (cp >= 0x30 && cp <= 0x7E) { _h.EscDispatch(string.Empty, (char)cp); _state = State.Ground; return; }
                if (cp == 0x7F) return;
                _state = State.Ground; Advance(cp); // non-ASCII after ESC: abort and print
                return;

            case State.EscapeIntermediate:
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); return; }
                if (cp >= 0x30 && cp <= 0x7E) { _h.EscDispatch(_intermediates.ToString(), (char)cp); _state = State.Ground; return; }
                if (cp == 0x7F) return;
                _state = State.Ground;
                return;

            case State.CsiEntry:
            case State.CsiParam:
                if (cp >= '0' && cp <= '9') { _params.Digit(cp - '0'); _state = State.CsiParam; return; }
                if (cp == ';') { _params.Next(false); _state = State.CsiParam; return; }
                if (cp == ':') { _params.Next(true); _state = State.CsiParam; return; }
                if (cp >= 0x3C && cp <= 0x3F)
                {
                    if (_state == State.CsiEntry) { _private = (char)cp; _state = State.CsiParam; }
                    else _state = State.CsiIgnore;
                    return;
                }
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); _state = State.CsiIntermediate; return; }
                if (cp >= 0x40 && cp <= 0x7E) { DispatchCsi((char)cp); return; }
                if (cp == 0x7F) return;
                _state = State.CsiIgnore;
                return;

            case State.CsiIntermediate:
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); return; }
                if (cp >= 0x40 && cp <= 0x7E) { DispatchCsi((char)cp); return; }
                if (cp == 0x7F) return;
                _state = State.CsiIgnore;
                return;

            case State.CsiIgnore:
                if (cp >= 0x40 && cp <= 0x7E) _state = State.Ground;
                return;

            case State.DcsEntry:
            case State.DcsParam:
                if (cp >= '0' && cp <= '9') { _params.Digit(cp - '0'); _state = State.DcsParam; return; }
                if (cp == ';') { _params.Next(false); _state = State.DcsParam; return; }
                if (cp == ':') { _params.Next(true); _state = State.DcsParam; return; }
                if (cp >= 0x3C && cp <= 0x3F)
                {
                    if (_state == State.DcsEntry) { _private = (char)cp; _state = State.DcsParam; }
                    else { _str.Clear(); _escInString = false; _state = State.DcsIgnore; }
                    return;
                }
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); _state = State.DcsIntermediate; return; }
                if (cp >= 0x40 && cp <= 0x7E) { _dcsFinal = (char)cp; _str.Clear(); _escInString = false; _state = State.DcsPassthrough; return; }
                _str.Clear(); _escInString = false; _state = State.DcsIgnore;
                return;

            case State.DcsIntermediate:
                if (cp >= 0x20 && cp <= 0x2F) { _intermediates.Append((char)cp); return; }
                if (cp >= 0x40 && cp <= 0x7E) { _dcsFinal = (char)cp; _str.Clear(); _escInString = false; _state = State.DcsPassthrough; return; }
                _str.Clear(); _escInString = false; _state = State.DcsIgnore;
                return;
        }
    }

    private void DispatchCsi(char final)
    {
        _state = State.Ground;
        _h.CsiDispatch(_params, _private, _intermediates.ToString(), final);
    }

    private void OscChar(int cp)
    {
        if (_escInString)
        {
            _escInString = false;
            if (cp == '\\') { _state = State.Ground; _h.OscDispatch(_str.ToString()); return; }
            // ESC followed by something else: terminate the OSC (xterm behaviour) and reprocess.
            _state = State.Ground;
            _h.OscDispatch(_str.ToString());
            Advance(0x1B);
            Advance(cp);
            return;
        }
        if (cp == 0x07) { _state = State.Ground; _h.OscDispatch(_str.ToString()); return; }
        if (cp == 0x1B) { _escInString = true; return; }
        if (cp == 0x18 || cp == 0x1A) { _state = State.Ground; return; }
        if (cp < 0x20) return;
        if (_str.Length < MaxStringLength) AppendCp(_str, cp);
    }

    private void StringChar(int cp)
    {
        if (_escInString)
        {
            _escInString = false;
            if (cp == '\\') { FinishString(); return; }
            FinishString();
            Advance(0x1B);
            Advance(cp);
            return;
        }
        if (cp == 0x1B) { _escInString = true; return; }
        if (cp == 0x18 || cp == 0x1A) { _state = State.Ground; return; }
        if (cp == 0x07 && _state == State.SosPmApc) { _state = State.Ground; return; }
        if (_state == State.DcsPassthrough && _str.Length < MaxStringLength) AppendCp(_str, cp);
    }

    private void FinishString()
    {
        var st = _state;
        _state = State.Ground;
        if (st == State.DcsPassthrough) _h.DcsDispatch(_private, _params, _intermediates.ToString(), _dcsFinal, _str.ToString());
    }

    private static void AppendCp(StringBuilder sb, int cp)
    {
        if (cp < 0x10000) sb.Append((char)cp); else sb.Append(char.ConvertFromUtf32(cp));
    }
}
