using System.Text;
using SessionForge.Core.Import;
using SessionForge.Core.Models;
using SessionForge.Protocols.Ssh;

namespace SessionForge.Tests.Protocols;

public class KeyTests
{
    [Fact]
    public void PiWordsMatchBlowfishConstants()
    {
        var w = BcryptPbkdf.ComputePiWords();
        // Well-known Blowfish initial values: P[0], P[17], S0[0], S3[255].
        Assert.Equal(0x243F6A88u, w[0]);
        Assert.Equal(0x8979FB1Bu, w[17]);
        Assert.Equal(0xD1310BA6u, w[18]);
        Assert.Equal(0x3AC372E6u, w[^1]);
    }

    [Theory]
    [InlineData(SshKeyType.Ed25519)]
    [InlineData(SshKeyType.EcdsaP256)]
    [InlineData(SshKeyType.EcdsaP384)]
    [InlineData(SshKeyType.EcdsaP521)]
    public void GeneratedUnencryptedKeysParseWithSshNet(SshKeyType type)
    {
        var key = SshKeys.Generate(type, "test@example", null);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", key.PrivateKeyPem);
        var info = SshKeys.Inspect(key.PrivateKeyPem, null);
        Assert.Null(info.Error);
        Assert.False(info.NeedsPassphrase);
        Assert.Equal(key.Fingerprint, info.Fingerprint);
        Assert.Equal(key.PublicKeyLine.Split(' ')[1], info.PublicKey!.Split(' ')[1]);
    }

    [Fact]
    public void GeneratedRsaKeyParsesWithSshNet()
    {
        var key = SshKeys.Generate(SshKeyType.Rsa, "rsa", null, 2048);
        var info = SshKeys.Inspect(key.PrivateKeyPem, null);
        Assert.Null(info.Error);
        Assert.Equal("ssh-rsa", info.KeyType);
        Assert.Equal(key.Fingerprint, info.Fingerprint);
    }

    [Fact]
    public void EncryptedKeyRoundTripsThroughSshNetBcrypt()
    {
        // SSH.NET has its own independent bcrypt_pbkdf + aes256-ctr implementation: decrypting our output
        // with it validates our KDF and cipher.
        var key = SshKeys.Generate(SshKeyType.Ed25519, "enc", "correct horse");
        var needs = SshKeys.Inspect(key.PrivateKeyPem, null);
        Assert.True(needs.NeedsPassphrase);
        var ok = SshKeys.Inspect(key.PrivateKeyPem, "correct horse");
        Assert.Null(ok.Error);
        Assert.Equal(key.Fingerprint, ok.Fingerprint);
        var bad = SshKeys.Inspect(key.PrivateKeyPem, "wrong");
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public void EncryptedRsaKeyRoundTrips()
    {
        var key = SshKeys.Generate(SshKeyType.Rsa, "enc-rsa", "pw", 2048);
        Assert.Equal(key.Fingerprint, SshKeys.Inspect(key.PrivateKeyPem, "pw").Fingerprint);
    }

    [Fact]
    public void AesCtrCounterCarries()
    {
        var key = new byte[32];
        var iv = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var data = new byte[48];
        var c1 = SshKeys.AesCtr(key, iv, data);
        var c2 = SshKeys.AesCtr(key, iv, c1); // CTR is its own inverse
        Assert.Equal(data, c2);
        Assert.NotEqual(c1.AsSpan(0, 16).ToArray(), c1.AsSpan(16, 16).ToArray());
    }

    [Theory]
    [InlineData("server1", SessionKind.Ssh, "server1", 22, null)]
    [InlineData("root@10.0.0.5:2222", SessionKind.Ssh, "10.0.0.5", 2222, "root")]
    [InlineData("ssh://admin@[fe80::1]:2200", SessionKind.Ssh, "fe80::1", 2200, "admin")]
    [InlineData("rdp://winbox", SessionKind.Rdp, "winbox", 3389, null)]
    [InlineData("vnc://pi:5901", SessionKind.Vnc, "pi", 5901, null)]
    [InlineData("telnet://switch", SessionKind.Telnet, "switch", 23, null)]
    [InlineData("ssh deploy@build -p 2022", SessionKind.Ssh, "build", 2022, "deploy")]
    [InlineData("fe80::1", SessionKind.Ssh, "fe80::1", 22, null)]
    public void QuickConnectParses(string input, SessionKind kind, string host, int port, string? user)
    {
        Assert.True(QuickConnect.TryParse(input, out var s));
        Assert.Equal(kind, s!.Kind);
        Assert.Equal(host, s.Host);
        Assert.Equal(port, s.Port);
        Assert.Equal(user, s.Credential.Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gopher://x")]
    [InlineData("host:notaport")]
    [InlineData("host:70000")]
    [InlineData("two words")]
    public void QuickConnectRejects(string input) => Assert.False(QuickConnect.TryParse(input, out _));

    [Fact]
    public void QuickConnectSerial()
    {
        Assert.True(QuickConnect.TryParse("serial://com3:115200", out var s));
        Assert.Equal(SessionKind.Serial, s!.Kind);
        Assert.Equal("COM3", s.Serial.PortName);
        Assert.Equal(115200, s.Serial.BaudRate);
    }
}
