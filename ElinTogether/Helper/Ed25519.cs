using System;
using System.Numerics;
using System.Security.Cryptography;

namespace ElinTogether.Helper;

/// <summary>
///     Ed25519 signatures (RFC 8032), in plain C# over BigInteger: the game's Mono has no Ed25519 of its own and the
///     mod ships no native library. Slow next to a real implementation (some milliseconds a signature), which is
///     nothing for one signature per request to the world service. Not constant-time: the key signs requests to a
///     public service, it guards no secret of the player's machine. Nothing of the game in here (the test compiles
///     this file alone)
/// </summary>
internal static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    // the base point, in extended coordinates (X, Y, Z, T)
    private static readonly BigInteger[] B = Base();

    /// <summary>
    ///     A new private key: 32 random bytes (the seed of RFC 8032)
    /// </summary>
    internal static byte[] NewSeed()
    {
        var seed = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(seed);
        return seed;
    }

    internal static byte[] PublicKey(byte[] seed)
    {
        var (a, _) = Expand(seed);
        return Encode(Mul(a, B));
    }

    internal static byte[] Sign(byte[] seed, byte[] message)
    {
        var (a, prefix) = Expand(seed);
        var pub = Encode(Mul(a, B));
        var r = Mod(Hash(prefix, message), L);
        var big = Encode(Mul(r, B));
        var k = Mod(Hash(big, pub, message), L);
        var s = Mod(r + k * a, L);
        var signature = new byte[64];
        Buffer.BlockCopy(big, 0, signature, 0, 32);
        Buffer.BlockCopy(Le(s), 0, signature, 32, 32);
        return signature;
    }

    internal static bool Verify(byte[] pub, byte[] message, byte[] signature)
    {
        if (pub.Length != 32 || signature.Length != 64) {
            return false;
        }

        var a = Decode(pub);
        var rBytes = new byte[32];
        Buffer.BlockCopy(signature, 0, rBytes, 0, 32);
        var r = Decode(rBytes);
        var sBytes = new byte[32];
        Buffer.BlockCopy(signature, 32, sBytes, 0, 32);
        var s = FromLe(sBytes);
        if (a is null || r is null || s >= L) {
            return false;
        }

        var k = Mod(Hash(rBytes, pub, message), L);
        return Equal(Mul(s, B), Add(r, Mul(k, a)));
    }

    private static (BigInteger A, byte[] Prefix) Expand(byte[] seed)
    {
        if (seed.Length != 32) {
            throw new ArgumentException("an Ed25519 seed is 32 bytes");
        }

        using var sha = SHA512.Create();
        var h = sha.ComputeHash(seed);
        h[0] &= 248;
        h[31] &= 127;
        h[31] |= 64;
        var low = new byte[32];
        Buffer.BlockCopy(h, 0, low, 0, 32);
        var prefix = new byte[32];
        Buffer.BlockCopy(h, 32, prefix, 0, 32);
        return (FromLe(low), prefix);
    }

    private static BigInteger Hash(params byte[][] parts)
    {
        using var sha = SHA512.Create();
        foreach (var part in parts) {
            sha.TransformBlock(part, 0, part.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return FromLe(sha.Hash!);
    }

    private static BigInteger Mod(BigInteger x, BigInteger? m = null)
    {
        var n = m ?? P;
        var r = BigInteger.Remainder(x, n);
        return r.Sign < 0 ? r + n : r;
    }

    private static BigInteger Inv(BigInteger x)
    {
        return BigInteger.ModPow(Mod(x), P - 2, P);
    }

    private static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
    {
        var a = Mod((p[1] - p[0]) * (q[1] - q[0]));
        var b = Mod((p[1] + p[0]) * (q[1] + q[0]));
        var c = Mod(2 * p[3] * q[3] * D);
        var d = Mod(2 * p[2] * q[2]);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return [Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h)];
    }

    private static BigInteger[] Mul(BigInteger s, BigInteger[] p)
    {
        BigInteger[] q = [0, 1, 1, 0];
        while (s > 0) {
            if (!s.IsEven) {
                q = Add(q, p);
            }

            p = Add(p, p);
            s >>= 1;
        }

        return q;
    }

    private static bool Equal(BigInteger[] p, BigInteger[] q)
    {
        return Mod(p[0] * q[2] - q[0] * p[2]) == 0 && Mod(p[1] * q[2] - q[1] * p[2]) == 0;
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) {
            return null;
        }

        var x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2 == 0) {
            return sign == 0 ? 0 : null;
        }

        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) {
            x = Mod(x * SqrtM1);
        }

        if (Mod(x * x - x2) != 0) {
            return null;
        }

        return (int)(x & 1) != sign ? P - x : x;
    }

    private static byte[] Encode(BigInteger[] p)
    {
        var zi = Inv(p[2]);
        var x = Mod(p[0] * zi);
        var y = Mod(p[1] * zi);
        var bytes = Le(y);
        bytes[31] |= (byte)((int)(x & 1) << 7);
        return bytes;
    }

    private static BigInteger[]? Decode(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        var sign = copy[31] >> 7;
        copy[31] &= 127;
        var y = FromLe(copy);
        if (RecoverX(y, sign) is not { } x) {
            return null;
        }

        return [x, y, 1, Mod(x * y)];
    }

    private static BigInteger[] Base()
    {
        var y = Mod(4 * Inv(5));
        var x = RecoverX(y, 0)!.Value;
        return [x, y, 1, Mod(x * y)];
    }

    private static BigInteger FromLe(byte[] bytes)
    {
        // (unsigned: one zero byte more on top)
        var copy = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        return new BigInteger(copy);
    }

    private static byte[] Le(BigInteger x)
    {
        var raw = x.ToByteArray();
        var bytes = new byte[32];
        Buffer.BlockCopy(raw, 0, bytes, 0, Math.Min(32, raw.Length));
        return bytes;
    }
}
