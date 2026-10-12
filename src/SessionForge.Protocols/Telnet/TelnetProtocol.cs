using System.Text;

namespace SessionForge.Protocols.Telnet;

/// <summary>
/// Telnet (RFC 854) stream processor: strips IAC commands from incoming data and produces negotiation replies.
/// Supports ECHO, SUPPRESS-GO-AHEAD, BINARY, TERMINAL-TYPE (RFC 1091) and NAWS (RFC 1073). Pure logic — no I/O.
/// </summary>
public sealed class TelnetProtocol
{
    public const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240, GA = 249, NOP = 241;
    public const byte OptBinary = 0, OptEcho = 1, OptSga = 3, OptTType = 24, OptNaws = 31;

    private enum St { Data, Iac, Opt, Sb, SbIac }

    private St _state;
    private byte _verb;
    private readonly List<byte> _sb = new();
    private readonly HashSet<byte> _localEnabled = new();   // options we (client) perform
    private readonly HashSet<byte> _remoteEnabled = new();  // options server performs
    private int _cols = 80, _rows = 24;

    public TelnetProtocol(string terminalType) => TerminalType = terminalType;

    public string TerminalType { get; }
    public bool ServerEchoes => _remoteEnabled.Contains(OptEcho);
    public bool NawsActive => _localEnabled.Contains(OptNaws);

    /// <summary>Initial client offers sent right after connecting.</summary>
    public byte[] InitialNegotiation()
    {
        _localEnabled.Add(OptNaws);
        _localEnabled.Add(OptTType);
        _requestedRemote.Add(OptSga);
        _requestedRemote.Add(OptEcho);
        return [IAC, WILL, OptNaws, IAC, WILL, OptTType, IAC, DO, OptSga, IAC, DO, OptEcho];
    }

    /// <summary>Processes received bytes; returns application data and appends protocol replies to <paramref name="replies"/>.</summary>
    public byte[] Receive(ReadOnlySpan<byte> input, List<byte> replies)
    {
        var data = new List<byte>(input.Length);
        foreach (var b in input)
        {
            switch (_state)
            {
                case St.Data:
                    if (b == IAC) _state = St.Iac; else data.Add(b);
                    break;
                case St.Iac:
                    switch (b)
                    {
                        case IAC: data.Add(IAC); _state = St.Data; break;
                        case DO: case DONT: case WILL: case WONT: _verb = b; _state = St.Opt; break;
                        case SB: _sb.Clear(); _state = St.Sb; break;
                        default: _state = St.Data; break; // GA, NOP, AYT, etc.
                    }
                    break;
                case St.Opt:
                    Negotiate(_verb, b, replies);
                    _state = St.Data;
                    break;
                case St.Sb:
                    if (b == IAC) _state = St.SbIac; else if (_sb.Count < 4096) _sb.Add(b);
                    break;
                case St.SbIac:
                    if (b == SE) { Subnegotiation(replies); _state = St.Data; }
                    else if (b == IAC) { _sb.Add(IAC); _state = St.Sb; }
                    else _state = St.Data;
                    break;
            }
        }
        return data.ToArray();
    }

    // Options we asked the server to perform (DO sent) and are awaiting an answer for — no reply needed on WILL.
    private readonly HashSet<byte> _requestedRemote = new();

    /// <summary>
    /// Q-method-lite negotiation: reply only when our state actually changes, so option loops can't occur.
    /// </summary>
    private void Negotiate(byte verb, byte opt, List<byte> replies)
    {
        switch (verb)
        {
            case DO:
                if (opt is OptNaws or OptTType or OptBinary or OptSga)
                {
                    bool newlyEnabled = _localEnabled.Add(opt); // false when we already offered it (WILL sent)
                    if (newlyEnabled) replies.AddRange([IAC, WILL, opt]);
                    if (opt == OptNaws) replies.AddRange(NawsMessage());
                }
                else replies.AddRange([IAC, WONT, opt]);
                break;
            case DONT:
                if (_localEnabled.Remove(opt)) replies.AddRange([IAC, WONT, opt]);
                break;
            case WILL:
                if (opt is OptEcho or OptSga or OptBinary)
                {
                    bool requested = _requestedRemote.Remove(opt);
                    if (_remoteEnabled.Add(opt) && !requested) replies.AddRange([IAC, DO, opt]);
                }
                else replies.AddRange([IAC, DONT, opt]);
                break;
            case WONT:
                _requestedRemote.Remove(opt);
                if (_remoteEnabled.Remove(opt)) replies.AddRange([IAC, DONT, opt]);
                break;
        }
    }

    private void Subnegotiation(List<byte> replies)
    {
        // TERMINAL-TYPE SEND (1) → IS (0) <type>
        if (_sb.Count >= 2 && _sb[0] == OptTType && _sb[1] == 1)
        {
            replies.AddRange([IAC, SB, OptTType, 0]);
            replies.AddRange(Encoding.ASCII.GetBytes(TerminalType.ToUpperInvariant()));
            replies.AddRange([IAC, SE]);
        }
    }

    public byte[] SetWindowSize(int cols, int rows)
    {
        _cols = cols; _rows = rows;
        return NawsActive ? NawsMessage() : [];
    }

    private byte[] NawsMessage()
    {
        var msg = new List<byte> { IAC, SB, OptNaws };
        void Add16(int v)
        {
            byte hi = (byte)(v >> 8), lo = (byte)v;
            msg.Add(hi); if (hi == IAC) msg.Add(IAC);
            msg.Add(lo); if (lo == IAC) msg.Add(IAC);
        }
        Add16(_cols);
        Add16(_rows);
        msg.AddRange([IAC, SE]);
        return msg.ToArray();
    }

    /// <summary>Escapes outgoing data: IAC doubled; bare CR followed by NUL unless in binary mode (RFC 854).</summary>
    public byte[] EscapeOutgoing(ReadOnlySpan<byte> data, bool returnSendsNewline)
    {
        var o = new List<byte>(data.Length + 4);
        bool binary = _localEnabled.Contains(OptBinary);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == IAC) { o.Add(IAC); o.Add(IAC); continue; }
            o.Add(b);
            if (b == '\r' && !binary && (i + 1 >= data.Length || data[i + 1] != '\n'))
                o.Add(returnSendsNewline ? (byte)'\n' : (byte)0);
        }
        return o.ToArray();
    }
}
