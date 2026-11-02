using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     A paint layer of a team emblem (lead dev 2026-11-02: "think of paint.net"): 64x64 pixels drawn in the editor,
///     kept in the layer's typed id as <c>d:</c> and the deflated bytes in URL-safe base64 (no separator of the emblem
///     text can be in it). The bytes: the palette's size n (1..255), n colours RGBA, then 4096 palette indices, rows
///     top-down (index 0 and any colour of alpha 0 are clear). The baker draws it as a 64 px picture, so it moves,
///     turns, tints, outlines and shadows as any picture of the game. An older game's reader refuses the emblem and
///     draws the team's fallback mark, as for every field e3 gained
/// </summary>
internal static class EmblemPaint
{
    internal const int N = TeamEmblem.Grid;
    internal const string Prefix = "d:";

    // the longest code a paint layer may have (the whole emblem's text is held to TeamEmblem.MaxText)
    private const int MaxCode = 3600;

    private static readonly Dictionary<string, Color32[]?> _decoded = [];

    internal static bool Is(string thing) => thing.StartsWith(Prefix);

    /// <summary>A clear canvas</summary>
    internal static Color32[] Blank() => new Color32[N * N];

    /// <summary>The code of a canvas (rows top-down), null when it has more than 255 colours</summary>
    internal static string? Encode(Color32[] px)
    {
        var palette = new List<Color32> { new(0, 0, 0, 0) };
        var index = new Dictionary<uint, byte>();
        var bytes = new byte[N * N];
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

    /// <summary>The canvas of a code (rows top-down; a fresh copy), null when it is not one</summary>
    internal static Color32[]? Decode(string thing)
    {
        if (!_decoded.TryGetValue(thing, out var px)) {
            px = Read(thing);
            if (_decoded.Count > 64) {
                _decoded.Clear();
            }

            _decoded[thing] = px;
        }

        return px == null ? null : (Color32[])px.Clone();
    }

    /// <summary>A code reads as a canvas (the host's check before it keeps an emblem)</summary>
    internal static bool Valid(string thing) => Decode(thing) != null;

    private static Color32[]? Read(string thing)
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
            // (never more than the palette and the pixels: a crafted code cannot blow up)
            var limit = 1 + 255 * 4 + N * N;
            var buffer = new byte[limit + 1];
            var read = 0;
            int got;
            while (read <= limit && (got = deflate.Read(buffer, read, buffer.Length - read)) > 0) {
                read += got;
            }

            if (read < 1) {
                return null;
            }

            int n = buffer[0];
            if (n < 1 || read != 1 + n * 4 + N * N) {
                return null;
            }

            var palette = new Color32[n];
            for (var i = 0; i < n; i++) {
                palette[i] = new(buffer[1 + i * 4], buffer[2 + i * 4], buffer[3 + i * 4], buffer[4 + i * 4]);
            }

            palette[0] = new(0, 0, 0, 0);
            var px = new Color32[N * N];
            var start = 1 + n * 4;
            for (var i = 0; i < px.Length; i++) {
                var at = buffer[start + i];
                if (at >= n) {
                    return null;
                }

                px[i] = palette[at];
            }

            return px;
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Team emblem: a paint layer could not be read");
            return null;
        }
    }

    /// <summary>The canvas as the baker's source (rows bottom-up, as a texture holds them)</summary>
    internal static (Color32[] Pixels, int W, int H)? Source(string thing)
    {
        if (Decode(thing) is not { } px) {
            return null;
        }

        var flipped = new Color32[N * N];
        for (var y = 0; y < N; y++) {
            System.Array.Copy(px, y * N, flipped, (N - 1 - y) * N, N);
        }

        return (flipped, N, N);
    }
}
