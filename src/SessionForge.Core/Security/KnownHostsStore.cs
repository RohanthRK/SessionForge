using System.Security.Cryptography;
using SessionForge.Core.Storage;

namespace SessionForge.Core.Security;

public enum HostKeyStatus { Unknown, Trusted, Changed }

public sealed class KnownHostEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string KeyType { get; set; } = string.Empty;
    /// <summary>Base64 of the SSH wire-format public key blob.</summary>
    public string KeyBlob { get; set; } = string.Empty;
    public string FingerprintSha256 { get; set; } = string.Empty;
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public string? Comment { get; set; }
}

public sealed class KnownHostsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<KnownHostEntry> Hosts { get; set; } = [];
}

/// <summary>Trust-on-first-use host key database. Keys are matched on (host, port, key type).</summary>
public sealed class KnownHostsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private KnownHostsDocument _doc;

    public KnownHostsStore(string path)
    {
        _path = path;
        _doc = JsonFile.Load<KnownHostsDocument>(path);
    }

    public IReadOnlyList<KnownHostEntry> Entries { get { lock (_lock) return _doc.Hosts.ToList(); } }

    public static string Fingerprint(byte[] keyBlob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');

    public static string FingerprintMd5(byte[] keyBlob) =>
        string.Join(':', MD5.HashData(keyBlob).Select(b => b.ToString("x2")));

    private static string Norm(string host) => host.Trim().ToLowerInvariant();

    public HostKeyStatus Check(string host, int port, string keyType, byte[] keyBlob, out KnownHostEntry? existing)
    {
        lock (_lock)
        {
            var blob = Convert.ToBase64String(keyBlob);
            var matches = _doc.Hosts.Where(h => h.Host == Norm(host) && h.Port == port).ToList();
            existing = matches.FirstOrDefault(h => h.KeyType == keyType) ?? matches.FirstOrDefault();
            if (matches.Count == 0) return HostKeyStatus.Unknown;
            if (matches.Any(h => h.KeyType == keyType && h.KeyBlob == blob)) return HostKeyStatus.Trusted;
            // Same host, same key type, different key => changed (possible MITM).
            if (matches.Any(h => h.KeyType == keyType)) return HostKeyStatus.Changed;
            // Known host presenting a different key *type* we haven't seen: treat as unknown for this type
            // but report the existing entry so the UI can warn.
            return HostKeyStatus.Unknown;
        }
    }

    public void Trust(string host, int port, string keyType, byte[] keyBlob, bool replaceExisting)
    {
        lock (_lock)
        {
            if (replaceExisting) _doc.Hosts.RemoveAll(h => h.Host == Norm(host) && h.Port == port && h.KeyType == keyType);
            _doc.Hosts.Add(new KnownHostEntry
            {
                Host = Norm(host), Port = port, KeyType = keyType,
                KeyBlob = Convert.ToBase64String(keyBlob), FingerprintSha256 = Fingerprint(keyBlob),
            });
            JsonFile.Save(_path, _doc);
        }
    }

    public void Remove(KnownHostEntry entry)
    {
        lock (_lock)
        {
            _doc.Hosts.RemoveAll(h => h.Host == entry.Host && h.Port == entry.Port && h.KeyType == entry.KeyType && h.KeyBlob == entry.KeyBlob);
            JsonFile.Save(_path, _doc);
        }
    }

    /// <summary>Imports OpenSSH known_hosts lines (plain host patterns only; hashed entries are skipped). Returns count imported.</summary>
    public int ImportOpenSsh(string knownHostsText)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var raw in knownHostsText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith('@')) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                byte[] blob;
                try { blob = Convert.FromBase64String(parts[2]); } catch (FormatException) { continue; }
                foreach (var hostPattern in parts[0].Split(','))
                {
                    if (hostPattern.Contains('*') || hostPattern.Contains('?') || hostPattern.StartsWith('!')) continue;
                    string host = hostPattern; int port = 22;
                    if (hostPattern.StartsWith('[') && hostPattern.Contains("]:"))
                    {
                        int close = hostPattern.IndexOf(']');
                        host = hostPattern[1..close];
                        if (!int.TryParse(hostPattern[(close + 2)..], out port)) continue;
                    }
                    var b64 = parts[2];
                    if (_doc.Hosts.Any(h => h.Host == Norm(host) && h.Port == port && h.KeyBlob == b64)) continue;
                    _doc.Hosts.Add(new KnownHostEntry
                    {
                        Host = Norm(host), Port = port, KeyType = parts[1], KeyBlob = b64,
                        FingerprintSha256 = Fingerprint(blob), Comment = "imported from OpenSSH",
                    });
                    n++;
                }
            }
            if (n > 0) JsonFile.Save(_path, _doc);
        }
        return n;
    }
}
