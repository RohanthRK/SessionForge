using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using SessionForge.Core.Storage;

namespace SessionForge.Core.Security;

/// <summary>
/// Encrypted credential store.
/// <list type="bullet">
/// <item>A random 256-bit data-encryption key (DEK) encrypts the payload with AES-256-GCM.</item>
/// <item>The DEK is wrapped with a key derived from the master password via Argon2id (memory-hard).</item>
/// <item>Optionally the DEK is also protected with Windows DPAPI (CurrentUser) so the vault can open
/// without the master password on this Windows account ("remember on this computer").</item>
/// </list>
/// Changing the master password only re-wraps the DEK. Writes are atomic.
/// </summary>
public sealed class CredentialVault : IDisposable
{
    private static readonly byte[] KeyAad = Encoding.ASCII.GetBytes("SFV1-key");
    private static readonly byte[] PayloadAad = Encoding.ASCII.GetBytes("SFV1-payload");
    private static readonly byte[] DpapiEntropy = Encoding.ASCII.GetBytes("SessionForge.Vault.v1");
    private const int NonceSize = 12, TagSize = 16, KeySize = 32;

    private readonly string _path;
    private readonly KdfParameters _defaultKdf;
    private readonly object _lock = new();
    private VaultEnvelope? _envelope;
    private byte[]? _dek;
    private VaultPayload? _payload;
    private DateTime _lastActivity = DateTime.UtcNow;
    private Timer? _autoLockTimer;

    public CredentialVault(string path, KdfParameters? kdf = null)
    {
        _path = path;
        _defaultKdf = kdf ?? new KdfParameters();
        if (File.Exists(path)) _envelope = JsonFile.Load<VaultEnvelope>(path);
    }

    public event Action? LockStateChanged;
    public event Action? Changed;

    public bool Exists { get { lock (_lock) return _envelope != null; } }
    public bool IsUnlocked { get { lock (_lock) return _dek != null; } }
    public bool RemembersOnThisComputer { get { lock (_lock) return _envelope?.DpapiKey != null; } }

    /// <summary>Lock automatically after this period without vault access. Zero disables.</summary>
    public TimeSpan AutoLockAfter { get; set; } = TimeSpan.FromMinutes(30);

    #region Lifecycle

    public void Create(string masterPassword, bool rememberOnThisComputer)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        lock (_lock)
        {
            if (_envelope != null) throw new InvalidOperationException("A vault already exists.");
            var kdf = new KdfParameters
            {
                Algorithm = "argon2id",
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
                MemoryKiB = _defaultKdf.MemoryKiB,
                Iterations = _defaultKdf.Iterations,
                Parallelism = _defaultKdf.Parallelism,
            };
            var dek = RandomNumberGenerator.GetBytes(KeySize);
            var kek = DeriveKey(masterPassword, kdf);
            try
            {
                _envelope = new VaultEnvelope
                {
                    Kdf = kdf,
                    WrappedKey = Convert.ToBase64String(Seal(kek, dek, KeyAad)),
                    DpapiKey = rememberOnThisComputer ? ProtectWithDpapi(dek) : null,
                };
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
            _dek = dek;
            _payload = new VaultPayload();
            SaveUnlocked();
        }
        Activity();
        LockStateChanged?.Invoke();
    }

    public void Unlock(string masterPassword)
    {
        lock (_lock)
        {
            var env = _envelope ?? throw new InvalidOperationException("No vault exists yet.");
            var kek = DeriveKey(masterPassword, env.Kdf);
            byte[] dek;
            try
            {
                dek = Open(kek, Convert.FromBase64String(env.WrappedKey), KeyAad) ?? throw new VaultAuthenticationException();
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
            LoadPayload(dek);
        }
        Activity();
        LockStateChanged?.Invoke();
    }

    /// <summary>Unlocks using the DPAPI-protected key, if "remember on this computer" is enabled. Returns false otherwise.</summary>
    public bool TryUnlockWithWindows()
    {
        lock (_lock)
        {
            if (_dek != null) return true;
            if (_envelope?.DpapiKey is not { } protectedKey) return false;
            byte[] dek;
            try
            {
                dek = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return false; // different Windows account/machine, or the user's DPAPI keys were reset
            }
            try { LoadPayload(dek); }
            catch (VaultAuthenticationException) { return false; }
        }
        Activity();
        LockStateChanged?.Invoke();
        return true;
    }

    private void LoadPayload(byte[] dek)
    {
        var plain = Open(dek, Convert.FromBase64String(_envelope!.Payload), PayloadAad);
        if (plain == null)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new VaultAuthenticationException("The vault file is damaged or was modified.");
        }
        try
        {
            _payload = JsonSerializer.Deserialize<VaultPayload>(plain, JsonFile.Options) ?? new VaultPayload();
            _payload.Secrets = new Dictionary<string, string>(_payload.Secrets, StringComparer.Ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        _dek = dek;
    }

    /// <summary>Re-derives the key from a password and checks it, without changing state. For "confirm identity" prompts.</summary>
    public bool VerifyMasterPassword(string masterPassword)
    {
        VaultEnvelope env;
        lock (_lock) env = _envelope ?? throw new InvalidOperationException("No vault exists yet.");
        var kek = DeriveKey(masterPassword, env.Kdf);
        try
        {
            var dek = Open(kek, Convert.FromBase64String(env.WrappedKey), KeyAad);
            if (dek == null) return false;
            CryptographicOperations.ZeroMemory(dek);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public void Lock()
    {
        bool changed;
        lock (_lock)
        {
            changed = _dek != null;
            if (_dek != null) CryptographicOperations.ZeroMemory(_dek);
            _dek = null;
            _payload = null;
        }
        if (changed) LockStateChanged?.Invoke();
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        if (!VerifyMasterPassword(currentPassword)) throw new VaultAuthenticationException();
        lock (_lock)
        {
            var dek = _dek ?? throw new VaultLockedException();
            var kdf = new KdfParameters
            {
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
                MemoryKiB = _defaultKdf.MemoryKiB, Iterations = _defaultKdf.Iterations, Parallelism = _defaultKdf.Parallelism,
            };
            var kek = DeriveKey(newPassword, kdf);
            try
            {
                _envelope!.Kdf = kdf;
                _envelope.WrappedKey = Convert.ToBase64String(Seal(kek, dek, KeyAad));
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
            SaveUnlocked();
        }
    }

    public void SetRememberOnThisComputer(bool remember)
    {
        lock (_lock)
        {
            var dek = _dek ?? throw new VaultLockedException();
            _envelope!.DpapiKey = remember ? ProtectWithDpapi(dek) : null;
            SaveUnlocked();
        }
    }

    /// <summary>Destroys the vault file (used by "reset vault" after a forgotten master password).</summary>
    public void Destroy()
    {
        Lock();
        lock (_lock)
        {
            _envelope = null;
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + $".destroyed-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                File.Delete(_path);
            }
            if (File.Exists(_path + ".bak")) File.Delete(_path + ".bak");
        }
        LockStateChanged?.Invoke();
    }

    #endregion

    #region Secret access

    private VaultPayload Payload
    {
        get
        {
            var p = _payload ?? throw new VaultLockedException();
            _lastActivity = DateTime.UtcNow;
            return p;
        }
    }

    public string? GetSecret(string key) { lock (_lock) return Payload.Secrets.TryGetValue(key, out var v) ? v : null; }

    public void SetSecret(string key, string? value)
    {
        lock (_lock)
        {
            if (value == null) Payload.Secrets.Remove(key);
            else Payload.Secrets[key] = value;
            SaveUnlocked();
        }
        Changed?.Invoke();
    }

    public void RemoveSecretsWithPrefix(string prefix)
    {
        lock (_lock)
        {
            foreach (var k in Payload.Secrets.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Payload.Secrets.Remove(k);
            SaveUnlocked();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> SecretKeys { get { lock (_lock) return Payload.Secrets.Keys.ToList(); } }

    public IReadOnlyList<VaultCredential> Credentials { get { lock (_lock) return Payload.Credentials.Select(JsonFile.Clone).ToList(); } }

    public VaultCredential? GetCredential(Guid id) { lock (_lock) return Payload.Credentials.FirstOrDefault(c => c.Id == id) is { } c ? JsonFile.Clone(c) : null; }

    public void SaveCredential(VaultCredential credential)
    {
        lock (_lock)
        {
            credential.Modified = DateTimeOffset.UtcNow;
            var list = Payload.Credentials;
            int i = list.FindIndex(c => c.Id == credential.Id);
            if (i >= 0) list[i] = JsonFile.Clone(credential); else list.Add(JsonFile.Clone(credential));
            SaveUnlocked();
        }
        Changed?.Invoke();
    }

    public void DeleteCredential(Guid id)
    {
        lock (_lock) { Payload.Credentials.RemoveAll(c => c.Id == id); SaveUnlocked(); }
        Changed?.Invoke();
    }

    public IReadOnlyList<VaultKey> Keys { get { lock (_lock) return Payload.Keys.Select(JsonFile.Clone).ToList(); } }

    public VaultKey? GetKey(Guid id) { lock (_lock) return Payload.Keys.FirstOrDefault(k => k.Id == id) is { } k ? JsonFile.Clone(k) : null; }

    public void SaveKey(VaultKey key)
    {
        lock (_lock)
        {
            var list = Payload.Keys;
            int i = list.FindIndex(k => k.Id == key.Id);
            if (i >= 0) list[i] = JsonFile.Clone(key); else list.Add(JsonFile.Clone(key));
            SaveUnlocked();
        }
        Changed?.Invoke();
    }

    public void DeleteKey(Guid id)
    {
        lock (_lock) { Payload.Keys.RemoveAll(k => k.Id == id); SaveUnlocked(); }
        Changed?.Invoke();
    }

    #endregion

    #region Auto lock

    public void Activity() => _lastActivity = DateTime.UtcNow;

    /// <summary>Starts the background auto-lock check.</summary>
    public void StartAutoLock()
    {
        _autoLockTimer ??= new Timer(_ =>
        {
            if (AutoLockAfter > TimeSpan.Zero && IsUnlocked && DateTime.UtcNow - _lastActivity > AutoLockAfter) Lock();
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    #endregion

    #region Crypto

    private void SaveUnlocked()
    {
        var env = _envelope!;
        var json = JsonSerializer.SerializeToUtf8Bytes(_payload, JsonFile.Options);
        try
        {
            env.Payload = Convert.ToBase64String(Seal(_dek!, json, PayloadAad));
        }
        finally { CryptographicOperations.ZeroMemory(json); }
        env.Modified = DateTimeOffset.UtcNow;
        JsonFile.Save(_path, env);
    }

    internal static byte[] DeriveKey(string password, KdfParameters kdf)
    {
        if (!string.Equals(kdf.Algorithm, "argon2id", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported KDF '{kdf.Algorithm}'.");
        var pwd = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(pwd)
            {
                Salt = Convert.FromBase64String(kdf.Salt),
                MemorySize = Math.Clamp(kdf.MemoryKiB, 8, 4 * 1024 * 1024),
                Iterations = Math.Clamp(kdf.Iterations, 1, 100),
                DegreeOfParallelism = Math.Clamp(kdf.Parallelism, 1, 64),
            };
            return argon.GetBytes(KeySize);
        }
        finally { CryptographicOperations.ZeroMemory(pwd); }
    }

    /// <summary>AES-256-GCM. Output layout: nonce(12) | tag(16) | ciphertext.</summary>
    internal static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), aad);
        return output;
    }

    /// <summary>Returns null if authentication fails (wrong key or tampered data).</summary>
    internal static byte[]? Open(byte[] key, byte[] sealedData, byte[] aad)
    {
        if (sealedData.Length < NonceSize + TagSize) return null;
        var plain = new byte[sealedData.Length - NonceSize - TagSize];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(sealedData.AsSpan(0, NonceSize), sealedData.AsSpan(NonceSize + TagSize), sealedData.AsSpan(NonceSize, TagSize), plain, aad);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private static string ProtectWithDpapi(byte[] dek) =>
        Convert.ToBase64String(ProtectedData.Protect(dek, DpapiEntropy, DataProtectionScope.CurrentUser));

    #endregion

    public void Dispose()
    {
        _autoLockTimer?.Dispose();
        Lock();
    }
}
