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

    // a texture read whole costs a GPU round trip: a list of more textures than that is not judged (all kept)
    private const int MaxTextures = 64;

    // nor more than this long at once on the game's thread (review 2026-11-03): the rest is judged at the next call
    private const long BudgetMs = 400;

    // each picture's verdict by its typed id: its pixels' hash, 0 when it makes no sticker
    private static readonly Dictionary<string, ulong> _hash = [];

    /// <summary>
    ///     The list without the pictures that make no sticker, in its order (the first of the same pictures stays).
    ///     <paramref name="complete" />: every picture was judged (a list cut by the time budget is judged further at
    ///     the next call; the caller keeps only a complete one)
    /// </summary>
    internal static List<SpriteGrid.Entry> Keep(List<SpriteGrid.Entry> list, out bool complete)
    {
        var clock = Stopwatch.StartNew();
        try {
            complete = Judge(list, clock);
        } catch (System.Exception e) {
            EmpLog.Information(e, "Sticker sieve: the pictures could not be judged");
            complete = true;
        }

        var kept = new List<SpriteGrid.Entry>(list.Count);
        var seen = new HashSet<ulong>();
        foreach (var e in list) {
            if (Sieved.IndexOf(e.Value[0]) >= 0 && _hash.TryGetValue(e.Value, out var hash) && (hash == 0 || !seen.Add(hash))) {
                continue;
            }

            kept.Add(e);
        }

        EmpLog.Debug("Sticker sieve: {Kept} of {All} pictures kept in {Ms} ms (complete: {Complete})", kept.Count, list.Count,
            clock.ElapsedMilliseconds, complete);
        return kept;
    }

    /// <summary>
    ///     The verdicts of the list's pictures not judged yet, texture by texture, within the time budget: true when
    ///     none is left
    /// </summary>
    private static bool Judge(List<SpriteGrid.Entry> list, Stopwatch clock)
    {
        var todo = new Dictionary<Texture2D, List<(string Value, Rect Rect)>>();
        foreach (var e in list) {
            if (Sieved.IndexOf(e.Value[0]) < 0 || _hash.ContainsKey(e.Value)) {
                continue;
            }

            // (reading the pictures of a lazily read source takes time too)
            if (clock.ElapsedMilliseconds > BudgetMs) {
                return false;
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
                todo[tex] = items = [];
            }

            items.Add((e.Value, rect));
        }

        if (todo.Count > MaxTextures) {
            // (pictures each on a texture of their own: too many reads, all kept as they are, each its own picture)
            foreach (var items in todo.Values) {
                foreach (var (value, _) in items) {
                    _hash[value] = Unique(value);
                }
            }

            return true;
        }

        foreach (var (tex, items) in todo) {
            if (clock.ElapsedMilliseconds > BudgetMs) {
                return false;
            }

            var px = ReadAll(tex);
            foreach (var (value, rect) in items) {
                // (a texture that cannot be read: its pictures kept)
                _hash[value] = px != null ? Verdict(px, tex.width, tex.height, rect) : Unique(value);
            }
        }

        return true;
    }

    // a kept picture's own hash (never 0, never a pixels' one in practice): from its id
    private static ulong Unique(string value) => 0x8000000000000000UL | (uint)value.GetHashCode();

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
            EmpLog.Information(e, "Sticker sieve: the texture {Name} could not be read", tex.name);
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
