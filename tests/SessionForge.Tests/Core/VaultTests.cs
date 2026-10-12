using SessionForge.Core.Security;

namespace SessionForge.Tests.Core;

public sealed class VaultTests : IDisposable
{
    // Cheap KDF parameters keep tests fast; production defaults are 64 MiB / 3 iterations.
    private static readonly KdfParameters FastKdf = new() { MemoryKiB = 1024, Iterations = 1, Parallelism = 1 };
    private readonly string _dir = Directory.CreateTempSubdirectory("sfvault").FullName;
    private string PathOf => System.IO.Path.Combine(_dir, "vault.sfv");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void CreateStoreReopenAndRead()
    {
        using (var v = new CredentialVault(PathOf, FastKdf))
        {
            v.Create("correct horse battery staple", rememberOnThisComputer: false);
            v.SetSecret("session:1", "p@ss");
            v.SaveCredential(new VaultCredential { Name = "prod", Username = "root", Password = "s3cret" });
        }
        using var v2 = new CredentialVault(PathOf, FastKdf);
        Assert.True(v2.Exists);
        Assert.False(v2.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => v2.GetSecret("session:1"));
        v2.Unlock("correct horse battery staple");
        Assert.Equal("p@ss", v2.GetSecret("session:1"));
        Assert.Equal("s3cret", Assert.Single(v2.Credentials).Password);
    }

    [Fact]
    public void WrongPasswordIsRejected()
    {
        using var v = new CredentialVault(PathOf, FastKdf);
        v.Create("right", false);
        v.Lock();
        Assert.Throws<VaultAuthenticationException>(() => v.Unlock("wrong"));
        Assert.False(v.IsUnlocked);
        Assert.False(v.VerifyMasterPassword("wrong"));
        Assert.True(v.VerifyMasterPassword("right"));
    }

    [Fact]
    public void FileDoesNotContainPlaintext()
    {
        using var v = new CredentialVault(PathOf, FastKdf);
        v.Create("pw", false);
        v.SetSecret("k", "VERY-SECRET-VALUE-123");
        var bytes = File.ReadAllText(PathOf);
        Assert.DoesNotContain("VERY-SECRET-VALUE-123", bytes);
        Assert.DoesNotContain("\"k\"", bytes);
    }

    [Fact]
    public void TamperedPayloadIsDetected()
    {
        using (var v = new CredentialVault(PathOf, FastKdf))
        {
            v.Create("pw", false);
            v.SetSecret("k", "value");
        }
        var env = SessionForge.Core.Storage.JsonFile.Load<VaultEnvelope>(PathOf);
        var payload = Convert.FromBase64String(env.Payload);
        payload[^1] ^= 0x01;
        env.Payload = Convert.ToBase64String(payload);
        SessionForge.Core.Storage.JsonFile.Save(PathOf, env);
        using var v2 = new CredentialVault(PathOf, FastKdf);
        var ex = Assert.Throws<VaultAuthenticationException>(() => v2.Unlock("pw"));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void ChangeMasterPasswordKeepsData()
    {
        using var v = new CredentialVault(PathOf, FastKdf);
        v.Create("old", false);
        v.SetSecret("a", "1");
        v.ChangeMasterPassword("old", "new");
        v.Lock();
        Assert.Throws<VaultAuthenticationException>(() => v.Unlock("old"));
        v.Unlock("new");
        Assert.Equal("1", v.GetSecret("a"));
    }

    [Fact]
    public void RememberOnThisComputerUnlocksWithDpapi()
    {
        using (var v = new CredentialVault(PathOf, FastKdf))
        {
            v.Create("pw", rememberOnThisComputer: true);
            v.SetSecret("a", "dpapi");
        }
        using var v2 = new CredentialVault(PathOf, FastKdf);
        Assert.True(v2.RemembersOnThisComputer);
        Assert.True(v2.TryUnlockWithWindows());
        Assert.Equal("dpapi", v2.GetSecret("a"));
        v2.SetRememberOnThisComputer(false);
        v2.Lock();
        Assert.False(v2.TryUnlockWithWindows());
    }

    [Fact]
    public void KeysAndPrefixRemoval()
    {
        using var v = new CredentialVault(PathOf, FastKdf);
        v.Create("pw", false);
        v.SetSecret("session:x", "1");
        v.SetSecret("session:x:jump", "2");
        v.SetSecret("other", "3");
        v.RemoveSecretsWithPrefix("session:x");
        Assert.Equal(["other"], v.SecretKeys);
        var key = new VaultKey { Name = "k", PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----" };
        v.SaveKey(key);
        Assert.Equal("k", v.GetKey(key.Id)!.Name);
        v.DeleteKey(key.Id);
        Assert.Empty(v.Keys);
    }

    [Fact]
    public void PasswordStrengthOrdersSensibly()
    {
        Assert.Equal(StrengthLevel.VeryWeak, PasswordStrength.Evaluate("password").Level);
        Assert.True(PasswordStrength.Evaluate("Tr0ub4dor&3-horse-Battery!").Bits > PasswordStrength.Evaluate("abc12345").Bits);
    }
}
