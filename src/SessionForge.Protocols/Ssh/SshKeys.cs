using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Renci.SshNet;
using Renci.SshNet.Common;
using SessionForge.Core.Security;

namespace SessionForge.Protocols.Ssh;

public enum SshKeyType { Ed25519, Rsa, EcdsaP256, EcdsaP384, EcdsaP521 }

public sealed record GeneratedKey(string PrivateKeyPem, string PublicKeyLine, string Fingerprint, SshKeyType Type);

public sealed record KeyInfo(bool NeedsPassphrase, string? PublicKey, string? Fingerprint, string? Comment, string? KeyType, string? Error);

/// <summary>SSH key generation (OpenSSH openssh-key-v1 format, optionally passphrase-encrypted) and inspection.</summary>
public static class SshKeys
{
    private const string AuthMagic = "openssh-key-v1\0";
    private const int BcryptRounds = 16;

    public static GeneratedKey Generate(SshKeyType type, string comment, string? passphrase, int rsaBits = 4096)
    {
        byte[] publicBlob;
        byte[] privatePart;
        switch (type)
        {
            case SshKeyType.Ed25519:
            {
                var priv = new Ed25519PrivateKeyParameters(new SecureRandom());
                var seed = priv.GetEncoded();
                var pub = priv.GeneratePublicKey().GetEncoded();
                publicBlob = new SshWriter().String("ssh-ed25519").String(pub).ToArray();
                var sk = new byte[64];
                seed.CopyTo(sk, 0);
                pub.CopyTo(sk, 32);
                privatePart = new SshWriter().String("ssh-ed25519").String(pub).String(sk).ToArray();
                CryptographicOperations.ZeroMemory(seed);
                CryptographicOperations.ZeroMemory(sk);
                break;
            }
            case SshKeyType.Rsa:
            {
                if (rsaBits is < 2048 or > 16384) throw new ArgumentOutOfRangeException(nameof(rsaBits), "RSA keys must be 2048–16384 bits.");
                using var rsa = RSA.Create(rsaBits);
                var p = rsa.ExportParameters(true);
                publicBlob = new SshWriter().String("ssh-rsa").MPInt(p.Exponent).MPInt(p.Modulus).ToArray();
                privatePart = new SshWriter().String("ssh-rsa").MPInt(p.Modulus).MPInt(p.Exponent).MPInt(p.D).MPInt(p.InverseQ).MPInt(p.P).MPInt(p.Q).ToArray();
                break;
            }
            default:
            {
                var (curve, name, ident) = type switch
                {
                    SshKeyType.EcdsaP384 => (ECCurve.NamedCurves.nistP384, "ecdsa-sha2-nistp384", "nistp384"),
                    SshKeyType.EcdsaP521 => (ECCurve.NamedCurves.nistP521, "ecdsa-sha2-nistp521", "nistp521"),
                    _ => (ECCurve.NamedCurves.nistP256, "ecdsa-sha2-nistp256", "nistp256"),
                };
                using var ec = ECDsa.Create(curve);
                var p = ec.ExportParameters(true);
                var q = new byte[1 + p.Q.X!.Length + p.Q.Y!.Length];
                q[0] = 4;
                p.Q.X.CopyTo(q, 1);
                p.Q.Y.CopyTo(q, 1 + p.Q.X.Length);
                publicBlob = new SshWriter().String(name).String(ident).String(q).ToArray();
                privatePart = new SshWriter().String(name).String(ident).String(q).MPInt(p.D!).ToArray();
                break;
            }
        }

        var pem = EncodePrivateKey(publicBlob, privatePart, comment, passphrase);
        var keyTypeName = new SshReader(publicBlob).String();
        var publicLine = $"{keyTypeName} {Convert.ToBase64String(publicBlob)}{(string.IsNullOrWhiteSpace(comment) ? string.Empty : " " + comment.Trim())}";
        return new GeneratedKey(pem, publicLine, KnownHostsStore.Fingerprint(publicBlob), type);
    }

    private static string EncodePrivateKey(byte[] publicBlob, byte[] privatePart, string comment, string? passphrase)
    {
        bool encrypt = !string.IsNullOrEmpty(passphrase);
        int blockSize = encrypt ? 16 : 8;
        var check = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        var section = new SshWriter().UInt32(check).UInt32(check).Raw(privatePart).String(comment ?? string.Empty);
        var plain = new List<byte>(section.ToArray());
        for (byte pad = 1; plain.Count % blockSize != 0; pad++) plain.Add(pad);
        var plainBytes = plain.ToArray();

        var w = new SshWriter().Raw(Encoding.ASCII.GetBytes(AuthMagic));
        if (encrypt)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var kdfOptions = new SshWriter().String(salt).UInt32(BcryptRounds).ToArray();
            var pass = Encoding.UTF8.GetBytes(passphrase!);
            var keyIv = BcryptPbkdf.Derive(pass, salt, BcryptRounds, 32 + 16);
            CryptographicOperations.ZeroMemory(pass);
            var cipher = AesCtr(keyIv.AsSpan(0, 32).ToArray(), keyIv.AsSpan(32, 16).ToArray(), plainBytes);
            CryptographicOperations.ZeroMemory(keyIv);
            w.String("aes256-ctr").String("bcrypt").String(kdfOptions).UInt32(1).String(publicBlob).String(cipher);
        }
        else
        {
            w.String("none").String("none").String(ReadOnlySpan<byte>.Empty).UInt32(1).String(publicBlob).String(plainBytes);
        }
        CryptographicOperations.ZeroMemory(plainBytes);
        var b64 = Convert.ToBase64String(w.ToArray());
        var sb = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < b64.Length; i += 70) sb.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        sb.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return sb.ToString();
    }

    /// <summary>AES-CTR (big-endian 128-bit counter) built on AES-ECB.</summary>
    internal static byte[] AesCtr(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var counter = (byte[])iv.Clone();
        var output = new byte[data.Length];
        var block = new byte[16];
        for (int off = 0; off < data.Length; off += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            int n = Math.Min(16, data.Length - off);
            for (int i = 0; i < n; i++) output[off + i] = (byte)(data[off + i] ^ block[i]);
            for (int i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        return output;
    }

    /// <summary>Parses a private key (OpenSSH, PEM, PuTTY .ppk) with SSH.NET and returns its public key and fingerprint.</summary>
    public static KeyInfo Inspect(string privateKeyText, string? passphrase)
    {
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyText));
            using var pkf = passphrase == null ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, passphrase);
            var alg = pkf.HostKeyAlgorithms.First();
            var blob = alg.Data;
            var type = new SshReader(blob).String();
            var comment = pkf.Key.Comment;
            var line = $"{type} {Convert.ToBase64String(blob)}{(string.IsNullOrWhiteSpace(comment) ? string.Empty : " " + comment)}";
            return new KeyInfo(false, line, KnownHostsStore.Fingerprint(blob), comment, type, null);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return new KeyInfo(true, null, null, null, null, null);
        }
        catch (Exception ex) when (ex is SshException or CryptographicException or ArgumentException or InvalidDataException or FormatException or NotSupportedException)
        {
            return new KeyInfo(false, null, null, null, null, passphrase != null ? "Wrong passphrase, or unsupported key: " + ex.Message : "Unsupported or invalid private key: " + ex.Message);
        }
    }
}
