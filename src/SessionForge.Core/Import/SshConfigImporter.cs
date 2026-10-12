using System.Globalization;
using SessionForge.Core.Models;

namespace SessionForge.Core.Import;

/// <summary>
/// Imports concrete "Host" entries from an OpenSSH client config (wildcard patterns are applied as defaults,
/// not imported as sessions). Supports HostName, User, Port, IdentityFile, ProxyJump, LocalForward,
/// RemoteForward, DynamicForward, Compression, ForwardAgent, ForwardX11, ServerAliveInterval, ConnectTimeout.
/// </summary>
public static class SshConfigImporter
{
    private sealed class Block
    {
        public List<string> Patterns { get; } = [];
        public List<(string Key, string Value)> Options { get; } = [];
    }

    public static List<Session> Parse(string configText, string? homeDirectory = null)
    {
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var blocks = new List<Block>();
        var global = new Block();
        Block current = global;
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var (key, value) = SplitKeyValue(line);
            if (key.Length == 0) continue;
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                current = new Block();
                current.Patterns.AddRange(value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
                blocks.Add(current);
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                current = new Block(); // Match blocks are not evaluated; their options are ignored.
            }
            else current.Options.Add((key, value));
        }

        var sessions = new List<Session>();
        foreach (var block in blocks)
        {
            foreach (var alias in block.Patterns)
            {
                if (alias.Contains('*') || alias.Contains('?') || alias.StartsWith('!')) continue;
                // OpenSSH semantics: first obtained value wins, across all matching blocks in file order.
                var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var multi = new List<(string, string)>();
                void Apply(Block b)
                {
                    foreach (var (k, v) in b.Options)
                    {
                        if (k.EndsWith("Forward", StringComparison.OrdinalIgnoreCase) && !k.Equals("ForwardAgent", StringComparison.OrdinalIgnoreCase) && !k.Equals("ForwardX11", StringComparison.OrdinalIgnoreCase))
                            multi.Add((k, v));
                        else opts.TryAdd(k, v);
                    }
                }
                Apply(global);
                foreach (var b in blocks.Where(b => b.Patterns.Any(p => Matches(p, alias)) && !b.Patterns.Any(p => p.StartsWith('!') && Matches(p[1..], alias))))
                    Apply(b);
                sessions.Add(Build(alias, opts, multi, homeDirectory));
            }
        }
        return sessions;
    }

    private static (string, string) SplitKeyValue(string line)
    {
        int i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '=') i++;
        var key = line[..i];
        var rest = line[i..].TrimStart();
        if (rest.StartsWith('=')) rest = rest[1..].TrimStart();
        if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"') rest = rest[1..^1];
        return (key, rest);
    }

    internal static bool Matches(string pattern, string host)
    {
        // glob with * and ?
        int p = 0, h = 0, star = -1, mark = 0;
        while (h < host.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(host[h]))) { p++; h++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = h; }
            else if (star >= 0) { p = star + 1; h = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static Session Build(string alias, Dictionary<string, string> o, List<(string Key, string Value)> forwards, string home)
    {
        var s = new Session { Name = alias, Kind = SessionKind.Ssh, Host = o.GetValueOrDefault("HostName", alias).Replace("%h", alias, StringComparison.Ordinal) };
        if (o.TryGetValue("Port", out var port) && int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) s.Port = p;
        if (o.TryGetValue("User", out var user)) s.Credential.Username = user;
        if (o.TryGetValue("IdentityFile", out var id))
        {
            s.Ssh.Auth = AuthMethod.PublicKey;
            s.Ssh.PrivateKeyPath = ExpandPath(id, home);
        }
        if (o.TryGetValue("Compression", out var comp)) s.Ssh.Compression = comp.Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (o.TryGetValue("ForwardAgent", out var fa)) s.Ssh.AgentForwarding = fa.Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (o.TryGetValue("ForwardX11", out var fx)) s.Ssh.X11Forwarding = fx.Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (o.TryGetValue("ServerAliveInterval", out var sai) && int.TryParse(sai, out var ka)) s.Ssh.KeepAliveSeconds = ka;
        if (o.TryGetValue("ConnectTimeout", out var ct) && int.TryParse(ct, out var cto)) s.Ssh.ConnectTimeoutSeconds = cto;
        if (o.TryGetValue("RemoteCommand", out var rc) && !rc.Equals("none", StringComparison.OrdinalIgnoreCase)) s.Ssh.RemoteCommand = rc;
        if (o.TryGetValue("ProxyJump", out var pj) && !pj.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var hop in pj.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                s.Ssh.JumpHosts.Add(ParseHop(hop));
        }
        foreach (var (k, v) in forwards)
        {
            var parts = v.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (k.Equals("DynamicForward", StringComparison.OrdinalIgnoreCase) && parts.Length >= 1)
            {
                var (bindHost, bindPort) = ParseBind(parts[0]);
                s.Ssh.Forwards.Add(new PortForward { Kind = ForwardKind.Dynamic, BindAddress = bindHost, BindPort = bindPort });
            }
            else if (parts.Length >= 2)
            {
                var (bindHost, bindPort) = ParseBind(parts[0]);
                var (th, tp) = ParseBind(parts[1], defaultHost: "localhost");
                s.Ssh.Forwards.Add(new PortForward
                {
                    Kind = k.Equals("RemoteForward", StringComparison.OrdinalIgnoreCase) ? ForwardKind.Remote : ForwardKind.Local,
                    BindAddress = bindHost, BindPort = bindPort, TargetHost = th, TargetPort = tp,
                });
            }
        }
        s.Notes = "Imported from OpenSSH config";
        return s;
    }

    internal static JumpHost ParseHop(string hop)
    {
        var j = new JumpHost();
        if (hop.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) hop = hop[6..];
        int at = hop.LastIndexOf('@');
        if (at >= 0) { j.Credential.Username = hop[..at]; hop = hop[(at + 1)..]; }
        if (hop.StartsWith('[') && hop.Contains(']'))
        {
            int close = hop.IndexOf(']');
            j.Host = hop[1..close];
            if (close + 2 <= hop.Length && hop.Length > close + 1 && hop[close + 1] == ':' && int.TryParse(hop[(close + 2)..], out var pp)) j.Port = pp;
        }
        else
        {
            int colon = hop.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hop[(colon + 1)..], out var pp)) { j.Host = hop[..colon]; j.Port = pp; }
            else j.Host = hop;
        }
        return j;
    }

    private static (string Host, uint Port) ParseBind(string spec, string defaultHost = "127.0.0.1")
    {
        // forms: port | host:port | [v6]:port | host/port
        spec = spec.Replace('/', ':');
        if (uint.TryParse(spec, out var onlyPort)) return (defaultHost, onlyPort);
        if (spec.StartsWith('['))
        {
            int close = spec.IndexOf(']');
            if (close > 0 && uint.TryParse(spec[(close + 2)..], out var p6)) return (spec[1..close], p6);
        }
        int c = spec.LastIndexOf(':');
        if (c > 0 && uint.TryParse(spec[(c + 1)..], out var p)) return (spec[..c] is "*" or "" ? "0.0.0.0" : spec[..c], p);
        return (defaultHost, 0);
    }

    private static string ExpandPath(string path, string home)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(home, path[2..]);
        path = path.Replace("%d", home, StringComparison.Ordinal);
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}
