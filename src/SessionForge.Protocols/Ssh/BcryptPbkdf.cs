using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace SessionForge.Protocols.Ssh;

/// <summary>
/// bcrypt_pbkdf as used by OpenSSH for encrypted private keys (openssh-key-v1, kdf "bcrypt").
/// Blowfish's initial P-array and S-boxes are the hexadecimal digits of π; they are computed once at startup
/// (Machin's formula with BigInteger) instead of being embedded as constants.
/// </summary>
public static class BcryptPbkdf
{
    private static readonly Lazy<uint[]> PiWords = new(ComputePiWords);

    /// <summary>First 1042 32-bit words of the fractional hexadecimal expansion of π (18 P + 4×256 S).</summary>
    internal static uint[] ComputePiWords()
    {
        const int words = 18 + 4 * 256;
        int bits = words * 32 + 64; // guard bits
        var one = BigInteger.One << bits;
        // π = 16·atan(1/5) − 4·atan(1/239)
        static BigInteger Atan(int x, BigInteger unity)
        {
            BigInteger sum = 0, term = unity / x, x2 = (BigInteger)x * x;
            int n = 1, sign = 1;
            while (!term.IsZero)
            {
                sum += sign * (term / n);
                term /= x2;
                n += 2;
                sign = -sign;
            }
            return sum;
        }
        var pi = 16 * Atan(5, one) - 4 * Atan(239, one);
        var frac = pi - (3 * one);           // fractional part
        var result = new uint[words];
        for (int i = 0; i < words; i++)
        {
            frac <<= 32;
            var word = frac >> bits;
            result[i] = (uint)word;
            frac -= word << bits;
        }
        return result;
    }

    private sealed class Blowfish
    {
        public readonly uint[] P = new uint[18];
        public readonly uint[] S = new uint[1024];

        public Blowfish()
        {
            var w = PiWords.Value;
            Array.Copy(w, 0, P, 0, 18);
            Array.Copy(w, 18, S, 0, 1024);
        }

        private uint F(uint x) => ((S[x >> 24] + S[256 + ((x >> 16) & 0xFF)]) ^ S[512 + ((x >> 8) & 0xFF)]) + S[768 + (x & 0xFF)];

        public void Encipher(ref uint l, ref uint r)
        {
            uint xl = l, xr = r;
            xl ^= P[0];
            for (int i = 1; i <= 16; i += 2)
            {
                xr ^= F(xl) ^ P[i];
                xl ^= F(xr) ^ P[i + 1];
            }
            xr ^= P[17];
            l = xr;
            r = xl;
        }

        private static uint StreamWord(ReadOnlySpan<byte> data, ref int pos)
        {
            uint w = 0;
            for (int i = 0; i < 4; i++)
            {
                w = (w << 8) | data[pos];
                pos = (pos + 1) % data.Length;
            }
            return w;
        }

        public void Expand(ReadOnlySpan<byte> key)
        {
            int pos = 0;
            for (int i = 0; i < 18; i++) P[i] ^= StreamWord(key, ref pos);
            uint l = 0, r = 0;
            for (int i = 0; i < 18; i += 2) { Encipher(ref l, ref r); P[i] = l; P[i + 1] = r; }
            for (int i = 0; i < 1024; i += 2) { Encipher(ref l, ref r); S[i] = l; S[i + 1] = r; }
        }

        public void Expand(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
        {
            int kp = 0;
            for (int i = 0; i < 18; i++) P[i] ^= StreamWord(key, ref kp);
            int dp = 0;
            uint l = 0, r = 0;
            for (int i = 0; i < 18; i += 2)
            {
                l ^= StreamWord(data, ref dp); r ^= StreamWord(data, ref dp);
                Encipher(ref l, ref r); P[i] = l; P[i + 1] = r;
            }
            for (int i = 0; i < 1024; i += 2)
            {
                l ^= StreamWord(data, ref dp); r ^= StreamWord(data, ref dp);
                Encipher(ref l, ref r); S[i] = l; S[i + 1] = r;
            }
        }
    }

    private static readonly byte[] Magic = "OxychromaticBlowfishSwatDynamite"u8.ToArray();

    /// <summary>bcrypt_hash(sha2pass, sha2salt) → 32 bytes (OpenBSD bcrypt_pbkdf.c).</summary>
    private static byte[] BcryptHash(byte[] sha2pass, byte[] sha2salt)
    {
        var bf = new Blowfish();
        bf.Expand(sha2salt, sha2pass);
        for (int i = 0; i < 64; i++)
        {
            bf.Expand(sha2salt);
            bf.Expand(sha2pass);
        }
        var cdata = new uint[8];
        for (int i = 0; i < 8; i++) cdata[i] = BinaryPrimitives.ReadUInt32BigEndian(Magic.AsSpan(i * 4));
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 8; j += 2) bf.Encipher(ref cdata[j], ref cdata[j + 1]);
        var output = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), cdata[i]);
        return output;
    }

    public static byte[] Derive(byte[] password, byte[] salt, int rounds, int keyLength)
    {
        if (rounds < 1) throw new ArgumentOutOfRangeException(nameof(rounds));
        if (password.Length == 0 || salt.Length == 0 || keyLength <= 0 || keyLength > 1024) throw new ArgumentException("Invalid bcrypt_pbkdf parameters.");
        const int hashLen = 32;
        int stride = (keyLength + hashLen - 1) / hashLen;
        int amt = (keyLength + stride - 1) / stride;
        var key = new byte[keyLength];
        var sha2pass = SHA512.HashData(password);
        var countSalt = new byte[salt.Length + 4];
        salt.CopyTo(countSalt, 0);
        int remaining = keyLength;
        for (int count = 1; remaining > 0; count++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(countSalt.AsSpan(salt.Length), (uint)count);
            var sha2salt = SHA512.HashData(countSalt);
            var tmp = BcryptHash(sha2pass, sha2salt);
            var outBlock = (byte[])tmp.Clone();
            for (int i = 1; i < rounds; i++)
            {
                sha2salt = SHA512.HashData(tmp);
                tmp = BcryptHash(sha2pass, sha2salt);
                for (int j = 0; j < outBlock.Length; j++) outBlock[j] ^= tmp[j];
            }
            // Pseudo-random, non-linear output placement (as in OpenBSD's implementation).
            amt = Math.Min(amt, remaining);
            int i2;
            for (i2 = 0; i2 < amt; i2++)
            {
                int dest = i2 * stride + (count - 1);
                if (dest >= keyLength) break;
                key[dest] = outBlock[i2];
            }
            remaining -= i2;
        }
        return key;
    }
}
