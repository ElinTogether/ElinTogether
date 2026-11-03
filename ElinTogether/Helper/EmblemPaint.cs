using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     A paint layer (lead dev 2026-11-02: "think of paint.net"): pixels drawn in the editor, kept in the layer's typed
///     id as <c>d:</c> and the deflated bytes in URL-safe base64 (no separator of the emblem text can be in it). The
///     bytes: the palette's size n (1..255), n colours RGBA, then one palette index per pixel, rows top-down (index 0
///     is clear); a canvas that is not 64x64 starts with 0, its width - 1 and its height - 1 (a drawing's, up to
///     256x256). The baker draws it as a picture of that many pixels, so it moves, turns, tints, outlines and shadows
///     as any picture of the game. An older game's reader refuses the emblem and draws the team's fallback mark, as for
///     every field e3 gained
/// </summary>
internal static class EmblemPaint
{
    internal const int N = TeamEmblem.Grid;
    internal const string Prefix = "d:";

    // the longest code a paint layer may have (a drawing's whole text is held to Drawing.MaxText)
    internal const int MaxCode = 60000;

    private static readonly Dictionary<string, (Color32[] Px, int W, int H)?> _decoded = [];

    internal static bool Is(string thing) => thing.StartsWith(Prefix);

    /// <summary>A clear canvas</summary>
    internal static Color32[] Blank(int w = N, int h = N) => new Color32[w * h];

    /// <summary>The code of a canvas (rows top-down), null when it has more than 255 colours</summary>
    internal static string? Encode(Color32[] px, int w = N, int h = N)
    {
        var palette = new List<Color32> { new(0, 0, 0, 0) };
        var index = new Dictionary<uint, byte>();
        var bytes = new byte[w * h];
        for (var i = 0; i < bytes.Length; i++) {
            var c = px[i];
            if (c.a == 0) {
                continue;
            }

            var key = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
            if (!index.TryGetValue(key, out var at)) {
                if (palette.Count >= 255) {
                    return null;
                }

                at = (byte)palette.Count;
                index[key] = at;
                palette.Add(c);
            }

            bytes[i] = at;
        }

        using var raw = new MemoryStream();
        if (w != N || h != N) {
            raw.WriteByte(0);
            raw.WriteByte((byte)(w - 1));
            raw.WriteByte((byte)(h - 1));
        }

        raw.WriteByte((byte)palette.Count);
        foreach (var c in palette) {
            raw.WriteByte(c.r);
            raw.WriteByte(c.g);
            raw.WriteByte(c.b);
            raw.WriteByte(c.a);
        }

        raw.Write(bytes, 0, bytes.Length);
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, System.IO.Compression.CompressionLevel.Optimal, true)) {
            var all = raw.ToArray();
            deflate.Write(all, 0, all.Length);
        }

        var code = System.Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Prefix + code;
    }

    /// <summary>
    ///     The code of a canvas of more than 255 colours (a soft brush, a merged layer): its colours rounded, coarser
    ///     step by step, until they fit. Null when even the coarsest rounding has too many
    /// </summary>
    internal static string? EncodeReduced(Color32[] px, int w = N, int h = N)
    {
        foreach (var step in new[] { 8, 16, 32, 64 }) {
            var reduced = new Color32[px.Length];
            for (var i = 0; i < px.Length; i++) {
                var c = px[i];
                reduced[i] = c.a == 0 ? default : new(Round(c.r, step), Round(c.g, step), Round(c.b, step), c.a == 255 ? (byte)255 : Round(c.a, step));
            }

            if (Encode(reduced, w, h) is { } code) {
                return code;
            }
        }

        return null;

        static byte Round(byte v, int step) => (byte)Mathf.Clamp(Mathf.RoundToInt(v / (float)step) * step, 0, 255);
    }

    /// <summary>The canvas of a code (rows top-down; a fresh copy) and its size, null when it is not one</summary>
    internal static (Color32[] Px, int W, int H)? Decode(string thing)
    {
        if (!_decoded.TryGetValue(thing, out var got)) {
            got = Read(thing);
            if (_decoded.Count > 64) {
                _decoded.Clear();
            }

            _decoded[thing] = got;
        }

        return got is { } g ? ((Color32[])g.Px.Clone(), g.W, g.H) : null;
    }

    /// <summary>A code reads as a canvas no larger than <paramref name="maxSide" /> (the host's check before it keeps an emblem)</summary>
    internal static bool Valid(string thing, int maxSide = N) => Decode(thing) is { } d && d.W <= maxSide && d.H <= maxSide;

    private static (Color32[] Px, int W, int H)? Read(string thing)
    {
        if (!Is(thing) || thing.Length > Prefix.Length + MaxCode || thing.Length <= Prefix.Length) {
            return null;
        }

        try {
            var b64 = thing.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/');
            foreach (var ch in b64) {
                if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')) {
                    return null;
                }
            }

            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var packed = new MemoryStream(System.Convert.FromBase64String(b64));
            using var deflate = new DeflateStream(packed, CompressionMode.Decompress);
            // (never more than a 256x256 canvas: a crafted code cannot blow up)
            var limit = 4 + 255 * 4 + Drawing.MaxSide * Drawing.MaxSide;
            var buffer = new byte[limit + 1];
            var read = 0;
            int got;
            while (read <= limit && (got = deflate.Read(buffer, read, buffer.Length - read)) > 0) {
                read += got;
            }

            if (read < 1) {
                return null;
            }

            int w = N, h = N, at = 0;
            if (buffer[0] == 0) {
                if (read < 4) {
                    return null;
                }

                (w, h, at) = (buffer[1] + 1, buffer[2] + 1, 3);
            }

            int n = buffer[at];
            if (n < 1 || read != at + 1 + n * 4 + w * h) {
                return null;
            }

            var palette = new Color32[n];
            for (var i = 0; i < n; i++) {
                var o = at + 1 + i * 4;
                palette[i] = new(buffer[o], buffer[o + 1], buffer[o + 2], buffer[o + 3]);
            }

            palette[0] = new(0, 0, 0, 0);
            var px = new Color32[w * h];
            var start = at + 1 + n * 4;
            for (var i = 0; i < px.Length; i++) {
                var k = buffer[start + i];
                if (k >= n) {
                    return null;
                }

                px[i] = palette[k];
            }

            return (px, w, h);
        } catch (System.Exception e) {
            EmpLog.Information(e, "Team emblem: a paint layer could not be read");
            return null;
        }
    }

    /// <summary>The canvas as the baker's source (rows bottom-up, as a texture holds them)</summary>
    internal static (Color32[] Pixels, int W, int H)? Source(string thing)
    {
        if (Decode(thing) is not { } d) {
            return null;
        }

        var flipped = new Color32[d.W * d.H];
        for (var y = 0; y < d.H; y++) {
            System.Array.Copy(d.Px, y * d.W, flipped, (d.H - 1 - y) * d.W, d.W);
        }

        return (flipped, d.W, d.H);
    }

    /// <summary>A paint layer covering a whole canvas of that size, clear (its size: the canvas' longer side)</summary>
    internal static EmblemLayer? Layer(int w, int h)
    {
        if (Encode(Blank(w, h), w, h) is not { } code) {
            return null;
        }

        return new(code, w / 2, h / 2, 100 * Mathf.Max(w, h) / N, 0, 0, TeamEmblem.Own);
    }
}
