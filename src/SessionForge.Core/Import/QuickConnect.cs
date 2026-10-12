using System.Globalization;
using SessionForge.Core.Models;

namespace SessionForge.Core.Import;

/// <summary>
/// Parses quick-connect strings into a transient session:
/// <c>[scheme://][user@]host[:port]</c> with schemes ssh, sftp, telnet, rdp, vnc, raw;
/// <c>serial://COM3[:baud]</c>; IPv6 literals in brackets (<c>[::1]:2222</c>).
/// </summary>
public static class QuickConnect
{
    public static bool TryParse(string? input, out Session? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        var kind = SessionKind.Ssh;
        int sep = text.IndexOf("://", StringComparison.Ordinal);
        if (sep > 0)
        {
            var scheme = text[..sep].ToLowerInvariant();
            text = text[(sep + 3)..].TrimEnd('/');
            kind = scheme switch
            {
                "ssh" => SessionKind.Ssh,
                "sftp" or "scp" => SessionKind.Sftp,
                "telnet" => SessionKind.Telnet,
                "rdp" or "mstsc" => SessionKind.Rdp,
                "vnc" => SessionKind.Vnc,
                "raw" or "tcp" => SessionKind.Raw,
                "serial" or "com" => SessionKind.Serial,
                _ => (SessionKind)(-1),
            };
            if ((int)kind < 0) return false;
        }
        else if (text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase))
        {
            // Accept "ssh user@host -p 2222"
            var parts = text[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? hostPart = null; int? p = null;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "-p" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out var pp)) { p = pp; i++; }
                else if (!parts[i].StartsWith('-')) hostPart = parts[i];
            }
            if (hostPart == null) return false;
            text = p is { } explicitPort ? $"{hostPart}:{explicitPort}" : hostPart;
        }

        if (kind == SessionKind.Serial)
        {
            var sp = text.Split(':');
            session = new Session { Kind = SessionKind.Serial, Name = text };
            session.Serial.PortName = sp[0].ToUpperInvariant();
            if (sp.Length > 1 && int.TryParse(sp[1], NumberStyles.None, CultureInfo.InvariantCulture, out var baud)) session.Serial.BaudRate = baud;
            return true;
        }

        string? user = null;
        int at = text.LastIndexOf('@');
        if (at > 0) { user = text[..at]; text = text[(at + 1)..]; }
        if (text.Length == 0) return false;

        string host;
        int port = Session.DefaultPort(kind);
        if (kind == SessionKind.Raw) port = 0;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0) return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.StartsWith(':') && !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                host = text[..colon];
                if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
            }
            else host = text; // bare IPv6 without brackets: no port
        }
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace)) return false;
        if (port is <= 0 or > 65535) return false;

        session = new Session
        {
            Kind = kind,
            Host = host,
            Port = port,
            Name = user == null ? host : $"{user}@{host}",
            Credential = new CredentialRef { Username = user },
        };
        return true;
    }
}
