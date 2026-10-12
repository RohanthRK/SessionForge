using SessionForge.Core.Models;

namespace SessionForge.Protocols.Ssh;

/// <summary>One SSH hop (jump host or final target) with everything needed to authenticate.</summary>
public sealed class SshEndpoint
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public string? Username { get; set; }
    public AuthMethod Auth { get; init; } = AuthMethod.Password;
    public string? PrivateKeyPath { get; init; }
    public Guid? VaultKeyId { get; init; }
    public Guid? CredentialId { get; init; }
    /// <summary>Prefix for vault keys of this hop, e.g. "session:{id}" or "session:{id}:jump0".</summary>
    public required string SecretScope { get; init; }
    public bool SavePasswordByDefault { get; init; }
    public int TimeoutSeconds { get; init; } = 20;
    public int KeepAliveSeconds { get; init; } = 30;
    public bool Compression { get; init; }
    public IReadOnlyList<string> KexAlgorithms { get; init; } = [];
    public IReadOnlyList<string> Ciphers { get; init; } = [];
    public IReadOnlyList<string> HostKeyAlgorithms { get; init; } = [];
    public bool TryAgent { get; init; }

    public string Display => $"{(string.IsNullOrEmpty(Username) ? "" : Username + "@")}{Host}{(Port == 22 ? "" : ":" + Port)}";

    public static SshEndpoint FromSession(Session s, bool useAgentFallback) => new()
    {
        Host = s.Host,
        Port = s.Port <= 0 ? 22 : s.Port,
        Username = s.Credential.Username,
        Auth = s.Ssh.Auth,
        PrivateKeyPath = s.Ssh.PrivateKeyPath,
        VaultKeyId = s.Ssh.VaultKeyId,
        CredentialId = s.Credential.CredentialId,
        SecretScope = $"session:{s.Id}",
        SavePasswordByDefault = s.Credential.SavePassword,
        TimeoutSeconds = s.Ssh.ConnectTimeoutSeconds,
        KeepAliveSeconds = s.Ssh.KeepAliveSeconds,
        Compression = s.Ssh.Compression,
        KexAlgorithms = s.Ssh.KexAlgorithms,
        Ciphers = s.Ssh.Ciphers,
        HostKeyAlgorithms = s.Ssh.HostKeyAlgorithms,
        TryAgent = useAgentFallback || s.Ssh.Auth == AuthMethod.Agent,
    };

    /// <summary>
    /// Builds the hop list for a session: each jump host (resolving references to other saved sessions,
    /// including their own jump chains, with cycle protection), then the target itself.
    /// </summary>
    public static List<SshEndpoint> BuildChain(Session target, Func<Guid, Session?> resolve, bool useAgentFallback)
    {
        var chain = new List<SshEndpoint>();
        AppendChain(target, resolve, useAgentFallback, chain, new HashSet<Guid>());
        return chain;
    }

    private static void AppendChain(Session s, Func<Guid, Session?> resolve, bool agent, List<SshEndpoint> chain, HashSet<Guid> visiting)
    {
        if (!visiting.Add(s.Id)) throw new InvalidOperationException($"Jump host loop detected at session '{s.Name}'.");
        for (int i = 0; i < s.Ssh.JumpHosts.Count; i++)
        {
            var j = s.Ssh.JumpHosts[i];
            if (j.SessionId is { } jid)
            {
                var js = resolve(jid) ?? throw new InvalidOperationException($"Jump host session {jid} referenced by '{s.Name}' no longer exists.");
                AppendChain(js, resolve, agent, chain, visiting);
            }
            else
            {
                chain.Add(new SshEndpoint
                {
                    Host = j.Host,
                    Port = j.Port <= 0 ? 22 : j.Port,
                    Username = j.Credential.Username,
                    Auth = j.Auth,
                    PrivateKeyPath = j.PrivateKeyPath,
                    CredentialId = j.Credential.CredentialId,
                    SecretScope = $"session:{s.Id}:jump{i}",
                    SavePasswordByDefault = j.Credential.SavePassword,
                    TimeoutSeconds = s.Ssh.ConnectTimeoutSeconds,
                    KeepAliveSeconds = s.Ssh.KeepAliveSeconds,
                    TryAgent = agent || j.Auth == AuthMethod.Agent,
                });
            }
        }
        chain.Add(FromSession(s, agent));
        visiting.Remove(s.Id);
    }
}
