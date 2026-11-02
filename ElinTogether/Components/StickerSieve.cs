using System.Collections.Generic;
using System.Diagnostics;
using ElinTogether.Helper;
using UnityEngine;

namespace ElinTogether.Components;

/// <summary>
///     The stickers' grid without the pictures that make no sticker (lead dev 2026-11-02: "some categories are
///     useless"; 2598 pictures, many of them pieces of the game's UI atlas). A picture of the game's items, monsters,
///     icons, UI icons and map objects is left out when it is blank, tiny (under 6 pixels across), a strip (more than
///     four times longer than wide), faint (under 24 solid pixels), flat (fewer than 3 colours, or fewer than 6 over most
///     of its box: a plain panel), a panel of one shade, a full rectangle (a texture), or the same pixels as one already
///     listed. The rule was tuned on the game's 1360 atlas sprites (762 of them kept). Each texture is read once; the
///     verdicts are kept for the session (they do not depend on the language). Portraits, places and gods' symbols are
///     pictures made to be shown: never sieved
/// </summary>
internal static class StickerSieve
{
    // the sources sieved (t things, c monsters, e a icons, u v UI icons, o map objects)
    private const string Sieved = "tceauvo";

    // a texture read whole costs a GPU round trip: past that many textures in one list, its pictures stay unjudged
    private const int MaxTextures = 64;

    // nor more than this long for one list (the rest stays unjudged, kept)
    private const long BudgetMs = 1500;

    // each picture's verdict by its typed id: its pixels' hash, 0 when it makes no sticker
    private static readonly Dictionary<string, ulong> _hash = [];

    /// <summary>The list without the pictures that make no sticker, in its order (the first of the same pictures stays)</summary>
    internal static List<SpriteGrid.Entry> Keep(List<SpriteGrid.Entry> list)
    {
        var clock = Stopwatch.StartNew();
        try {
            Judge(list, clock);
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Sticker sieve: the pictures could not be judged");
        }

        var kept = new List<SpriteGrid.Entry>(list.Count);
        var seen = new HashSet<ulong>();
        foreach (var e in list) {
            if (Sieved.IndexOf(e.Value[0]) >= 0 && _hash.TryGetValue(e.Value, out var hash) && (hash == 0 || !seen.Add(hash))) {
                continue;
            }

            kept.Add(e);
        }

        EmpLog.Debug("Sticker sieve: {Kept} of {All} pictures kept in {Ms} ms", kept.Count, list.Count, clock.ElapsedMilliseconds);
        return kept;
    }

    /// <summary>The verdicts of the list's pictures not judged yet, texture by texture</summary>
    private static void Judge(List<SpriteGrid.Entry> list, Stopwatch clock)
    {
        var todo = new Dictionary<Texture2D, List<(string Value, Rect Rect)>>();
        foreach (var e in list) {
            if (Sieved.IndexOf(e.Value[0]) < 0 || _hash.ContainsKey(e.Value)) {
                continue;
            }

            var sprite = e.Picture();
            if (sprite == null) {
                _hash[e.Value] = 0;
                continue;
            }

            Rect rect;
            try {
                // (a tightly packed sprite has no rectangle of its own: left unjudged)
                rect = sprite.textureRect;
            } catch (System.Exception) {
                continue;
            }

            var tex = sprite.texture;
            if (tex == null) {
                continue;
            }

            if (!todo.TryGetValue(tex, out var items)) {
                if (todo.Count >= MaxTextures) {
                    continue;
                }

                todo[tex] = items = [];
            }

            items.Add((e.Value, rect));
        }

        foreach (var (tex, items) in todo) {
            if (clock.ElapsedMilliseconds > BudgetMs) {
                return;
            }

            if (ReadAll(tex) is not { } px) {
                continue;
            }

            foreach (var (value, rect) in items) {
                _hash[value] = Verdict(px, tex.width, tex.height, rect);
            }
        }
    }

    /// <summary>A texture's pixels (rows bottom-up), through a render texture when it is not readable; null when it cannot be read</summary>
    private static Color32[]? ReadAll(Texture2D tex)
    {
        try {
            if (tex.isReadable) {
                return tex.GetPixels32();
            }
        } catch (System.Exception) {
            // (a readable texture of a format GetPixels32 does not read: through the render texture)
        }

        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        Texture2D? copy = null;
        try {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply(false, false);
            return copy.GetPixels32();
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Sticker sieve: the texture {Name} could not be read", tex.name);
            return null;
        } finally {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            if (copy != null) {
                Object.Destroy(copy);
            }
        }
    }

    /// <summary>A picture's pixels' hash (never 0), or 0 when it makes no sticker</summary>
    internal static ulong Verdict(Color32[] px, int texW, int texH, Rect rect)
    {
        int x0 = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, texW), y0 = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, texH);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(rect.xMax), x0, texW), y1 = Mathf.Clamp(Mathf.RoundToInt(rect.yMax), y0, texH);
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1, solid = 0;
        var colours = new HashSet<int>();
        double sum = 0, sumSq = 0;
        // (FNV-1a over the size and the pixels, the clear ones as one)
        var hash = 14695981039346656037UL;
        void Mix(uint v)
        {
            hash ^= v;
            hash *= 1099511628211UL;
        }

        Mix((uint)(x1 - x0));
        Mix((uint)(y1 - y0));
        for (var y = y0; y < y1; y++) {
            for (var x = x0; x < x1; x++) {
                var c = px[y * texW + x];
                if (c.a < 40) {
                    Mix(0);
                    continue;
                }

                Mix(((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a);
                bx0 = Mathf.Min(bx0, x);
                bx1 = Mathf.Max(bx1, x);
                by0 = Mathf.Min(by0, y);
                by1 = Mathf.Max(by1, y);
                if (c.a < 128) {
                    continue;
                }

                solid++;
                if (colours.Count < 6) {
                    colours.Add(((c.r >> 3) << 10) | ((c.g >> 3) << 5) | (c.b >> 3));
                }

                var lum = 0.3 * c.r + 0.59 * c.g + 0.11 * c.b;
                sum += lum;
                sumSq += lum * lum;
            }
        }

        return Worth(bx1 < 0 ? 0 : bx1 - bx0 + 1, by1 < 0 ? 0 : by1 - by0 + 1, solid, colours.Count, sum, sumSq) ? (hash == 0 ? 1UL : hash) : 0UL;
    }

    /// <summary>
    ///     The rule: the opaque box's width and height, its solid pixels, its colours (counted up to 6) and the sum and
    ///     the sum of squares of the solid pixels' brightness (0..255)
    /// </summary>
    internal static bool Worth(int w, int h, int solid, int colours, double sum, double sumSq)
    {
        if (w <= 0 || h <= 0 || Mathf.Min(w, h) < 6 || Mathf.Max(w, h) > 4 * Mathf.Min(w, h) || solid < 24) {
            return false;
        }

        var fill = solid / (float)(w * h);
        if (colours < 3 || colours < 6 && fill >= 0.7f) {
            return false;
        }

        var mean = sum / solid;
        var sd = System.Math.Sqrt(System.Math.Max(0.0, sumSq / solid - mean * mean));
        return !(fill >= 0.85f && sd < 20.0) && !(fill >= 0.97f && w * h >= 400);
    }

#if DEBUG
    /// <summary>The bench: how many verdicts are known, and how many of them leave the picture out</summary>
    internal static string BenchState => _hash.Count + "|" + System.Linq.Enumerable.Count(_hash.Values, v => v == 0);

    /// <summary>The bench: the verdict of one typed id ("kept", "out", or "-" when not judged)</summary>
    internal static string BenchVerdict(string value) => _hash.TryGetValue(value, out var h) ? h == 0 ? "out" : "kept" : "-";
#endif
}
