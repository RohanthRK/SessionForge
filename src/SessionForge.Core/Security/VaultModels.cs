namespace SessionForge.Core.Security;

/// <summary>A named, reusable credential (username + password) that sessions can reference.</summary>
public sealed class VaultCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An SSH private key held inside the vault (OpenSSH/PuTTY/PEM text) with optional passphrase.</summary>
public sealed class VaultKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string? Passphrase { get; set; }
    public string? PublicKey { get; set; }
    public string? Fingerprint { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The decrypted vault payload. Never written to disk unencrypted.</summary>
public sealed class VaultPayload
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Free-form secrets keyed by purpose, e.g. "session:{id}", "passphrase:{path}", "proxy:{id}", "jump:{id}:{n}".</summary>
    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
    public List<VaultCredential> Credentials { get; set; } = [];
    public List<VaultKey> Keys { get; set; } = [];
}

/// <summary>On-disk envelope. All binary fields are base64.</summary>
public sealed class VaultEnvelope
{
    public string Format { get; set; } = "SessionForge.Vault";
    public int Version { get; set; } = 1;
    public KdfParameters Kdf { get; set; } = new();
    /// <summary>Data-encryption key wrapped with the password-derived key (AES-256-GCM: nonce|tag|ciphertext).</summary>
    public string WrappedKey { get; set; } = string.Empty;
    /// <summary>Optional: data-encryption key protected with Windows DPAPI (CurrentUser) for "remember on this computer".</summary>
    public string? DpapiKey { get; set; }
    /// <summary>Payload encrypted with the data-encryption key (AES-256-GCM: nonce|tag|ciphertext).</summary>
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KdfParameters
{
    public string Algorithm { get; set; } = "argon2id";
    public string Salt { get; set; } = string.Empty;
    public int MemoryKiB { get; set; } = 65536;
    public int Iterations { get; set; } = 3;
    public int Parallelism { get; set; } = 4;
}

public sealed class VaultLockedException : InvalidOperationException
{
    public VaultLockedException() : base("The credential vault is locked.") { }
}

public sealed class VaultAuthenticationException : Exception
{
    public VaultAuthenticationException() : base("Incorrect master password.") { }
    public VaultAuthenticationException(string message) : base(message) { }
}
