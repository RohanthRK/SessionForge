using Microsoft.Win32;
using SessionForge.Core.Models;

namespace SessionForge.Core.Import;

/// <summary>Imports PuTTY saved sessions from HKCU\Software\SimonTatham\PuTTY\Sessions (SSH, Telnet, Serial, Raw).</summary>
public static class PuttyImporter
{
    private const string Root = @"Software\SimonTatham\PuTTY\Sessions";

    public static List<Session> ImportFromRegistry()
    {
        var list = new List<Session>();
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        if (root == null) return list;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k == null) continue;
            var values = k.GetValueNames().ToDictionary(v => v, v => k.GetValue(v), StringComparer.OrdinalIgnoreCase);
            var s = FromValues(Uri.UnescapeDataString(name), values);
            if (s != null) list.Add(s);
        }
        return list;
    }

    /// <summary>Maps a PuTTY session's registry values to a session. Returns null for unsupported or empty entries.</summary>
    public static Session? FromValues(string name, IReadOnlyDictionary<string, object?> v)
    {
        string Str(string key) => v.TryGetValue(key, out var o) && o is string s ? s : string.Empty;
        int Int(string key, int def) => v.TryGetValue(key, out var o) && o is int i ? i : def;

        var protocol = Str("Protocol").ToLowerInvariant();
        var host = Str("HostName");
        if (name == "Default Settings" && string.IsNullOrEmpty(host)) return null;

        var s = new Session { Name = name, Notes = "Imported from PuTTY" };
        switch (protocol)
        {
            case "ssh": s.Kind = SessionKind.Ssh; break;
            case "telnet": s.Kind = SessionKind.Telnet; break;
            case "raw": s.Kind = SessionKind.Raw; break;
            case "serial":
                s.Kind = SessionKind.Serial;
                s.Serial.PortName = Str("SerialLine") is { Length: > 0 } line ? line : "COM1";
                s.Serial.BaudRate = Int("SerialSpeed", 9600);
                s.Serial.DataBits = Int("SerialDataBits", 8);
                s.Serial.StopBits = Int("SerialStopHalfbits", 2) switch { 3 => SerialStopBits.OnePointFive, 4 => SerialStopBits.Two, _ => SerialStopBits.One };
                s.Serial.Parity = (SerialParity)Math.Clamp(Int("SerialParity", 0), 0, 4);
                s.Serial.FlowControl = Int("SerialFlowControl", 1) switch { 1 => SerialFlowControl.XonXoff, 2 => SerialFlowControl.RtsCts, 3 => SerialFlowControl.DsrDtr, _ => SerialFlowControl.None };
                return s;
            default: return null;
        }

        // PuTTY allows user@host in HostName.
        int at = host.LastIndexOf('@');
        if (at > 0) { s.Credential.Username = host[..at]; host = host[(at + 1)..]; }
        s.Host = host;
        if (string.IsNullOrWhiteSpace(s.Host)) return null;
        s.Port = Int("PortNumber", Session.DefaultPort(s.Kind));
        if (Str("UserName") is { Length: > 0 } user) s.Credential.Username = user;

        if (s.Kind == SessionKind.Ssh)
        {
            var key = Str("PublicKeyFile");
            if (key.Length > 0) { s.Ssh.Auth = AuthMethod.PublicKey; s.Ssh.PrivateKeyPath = key; }
            s.Ssh.Compression = Int("Compression", 0) == 1;
            s.Ssh.AgentForwarding = Int("AgentFwd", 0) == 1;
            s.Ssh.X11Forwarding = Int("X11Forward", 0) == 1;
            int ka = Int("PingIntervalSecs", 0);
            if (ka > 0) s.Ssh.KeepAliveSeconds = ka;
            if (Str("RemoteCommand") is { Length: > 0 } cmd) s.Ssh.RemoteCommand = cmd;
            s.Ssh.Forwards.AddRange(ParsePortForwardings(Str("PortForwardings")));
        }
        return s;
    }

    /// <summary>PuTTY format: comma-separated entries like "L8080=localhost:80", "R2222=host:22", "D1080", optional 4/6 prefix.</summary>
    public static IEnumerable<PortForward> ParsePortForwardings(string spec)
    {
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var e = raw;
            if (e.Length > 0 && (e[0] == '4' || e[0] == '6')) e = e[1..];
            if (e.Length < 2) continue;
            char type = e[0];
            var rest = e[1..];
            var eq = rest.IndexOf('=');
            var bind = eq >= 0 ? rest[..eq] : rest;
            string bindHost = "127.0.0.1";
            int bc = bind.LastIndexOf(':');
            if (bc > 0) { bindHost = bind[..bc]; bind = bind[(bc + 1)..]; }
            if (!uint.TryParse(bind, out var bindPort)) continue;
            if (type == 'D') { yield return new PortForward { Kind = ForwardKind.Dynamic, BindAddress = bindHost, BindPort = bindPort }; continue; }
            if (eq < 0) continue;
            var target = rest[(eq + 1)..];
            int tc = target.LastIndexOf(':');
            if (tc <= 0 || !uint.TryParse(target[(tc + 1)..], out var tport)) continue;
            yield return new PortForward
            {
                Kind = type == 'R' ? ForwardKind.Remote : ForwardKind.Local,
                BindAddress = bindHost, BindPort = bindPort, TargetHost = target[..tc], TargetPort = tport,
            };
        }
    }
}
