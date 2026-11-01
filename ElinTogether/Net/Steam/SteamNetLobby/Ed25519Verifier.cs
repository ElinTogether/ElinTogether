using System;
using System.Numerics;
using System.Security.Cryptography;

namespace ElinTogether.Net.Steam;

/// <summary>
///     Ed25519 signature check only (RFC 8032 section 5.1.7, pure Ed25519), for the public server's host certificate
///     (HostCertificate). Mono has no Ed25519; this is the textbook algorithm on BigInteger: a few milliseconds per
///     check, which the callers cache. Not constant time, which a check of public data does not need. The ETWorld
///     client (P2) brings its own signer; this file stays independent of it on purpose
/// </summary>
internal static class Ed25519Verifier
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point B = BasePoint();

    private readonly struct Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t)
    {
        public readonly BigInteger X = x, Y = y, Z = z, T = t;
    }

    internal static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey is not { Length: 32 } || signature is not { Length: 64 } || message is null) {
            return false;
        }

        try {
            var rBytes = new byte[32];
            Array.Copy(signature, 0, rBytes, 0, 32);
            var sBytes = new byte[32];
            Array.Copy(signature, 32, sBytes, 0, 32);
            if (Decompress(publicKey) is not { } a || Decompress(rBytes) is not { } r) {
                return false;
            }

            var s = FromLittleEndian(sBytes);
            if (s >= L) {
                return false;
            }

            byte[] digest;
            using (var sha = SHA512.Create()) {
                var input = new byte[64 + message.Length];
                Array.Copy(rBytes, 0, input, 0, 32);
                Array.Copy(publicKey, 0, input, 32, 32);
                Array.Copy(message, 0, input, 64, message.Length);
                digest = sha.ComputeHash(input);
            }

            var h = FromLittleEndian(digest) % L;
            var sB = Multiply(s, B);
            var q = Add(r, Multiply(h, a));
            return Mod(sB.X * q.Z - q.X * sB.Z).IsZero && Mod(sB.Y * q.Z - q.Y * sB.Z).IsZero;
        } catch (Exception) {
            return false;
        }
    }

    private static BigInteger Mod(BigInteger v)
    {
        var m = v % P;
        return m.Sign < 0 ? m + P : m;
    }

    private static BigInteger Inv(BigInteger v)
    {
        return BigInteger.ModPow(Mod(v), P - 2, P);
    }

    // (unsigned, little endian: a zero byte on top keeps BigInteger from reading a sign)
    private static BigInteger FromLittleEndian(byte[] bytes)
    {
        var copy = new byte[bytes.Length + 1];
        Array.Copy(bytes, copy, bytes.Length);
        return new(copy);
    }

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var hh = b + a;
        return new(Mod(e * f), Mod(g * hh), Mod(f * g), Mod(e * hh));
    }

    private static Point Multiply(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);
        while (s.Sign > 0) {
            if (!s.IsEven) {
                q = Add(q, p);
            }

            p = Add(p, p);
            s >>= 1;
        }

        return q;
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) {
            return null;
        }

        var x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero) {
            return sign != 0 ? null : BigInteger.Zero;
        }

        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) {
            x = Mod(x * SqrtM1);
        }

        if (!Mod(x * x - x2).IsZero) {
            return null;
        }

        if ((int)(x & 1) != sign) {
            x = P - x;
        }

        return x;
    }

    private static Point? Decompress(byte[] bytes)
    {
        var y = FromLittleEndian(bytes);
        var sign = (int)(y >> 255) & 1;
        y &= (BigInteger.One << 255) - 1;
        if (RecoverX(y, sign) is not { } x) {
            return null;
        }

        return new Point(x, y, 1, Mod(x * y));
    }

    private static Point BasePoint()
    {
        var y = Mod(4 * Inv(5));
        var x = RecoverX(y, 0)!.Value;
        return new(x, y, 1, Mod(x * y));
    }
}
