using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace ElinTogether.Helper;

/// <summary>
///     A text layer's letters as an alpha raster (lead dev 2026-11-01: text on the emblem). The game's own fonts
///     (<see cref="SkinManager" />'s bundled list, the same in every copy, never an OS font) rasterize each glyph on
///     the CPU (FreeType) into the font's atlas; the atlas is copied back once per text and the glyphs laid out here,
///     with the letter spacing, then bent along an arc. No camera, no frame to wait for; on a game without graphics
///     (the dedicated server) a text draws nothing. Cached by text, font, spacing and curve
/// </summary>
internal static class EmblemText
{
    /// <summary>The glyphs' size in pixels (the layer then scales the raster like a sprite)</summary>
    private const int Raster = 32;

    private const int Capacity = 64;
    private const int MaxSide = 512;

    private static readonly Dictionary<string, (float[] Alpha, int W, int H)?> _cache = [];

    /// <summary>The names of the game's own fonts, the first is the default ("" in a layer)</summary>
    internal static List<string> Fonts()
    {
        try {
            return SkinManager.Instance?.fontListOriginal?.Where(f => f != null && f.font != null && Usable(f._name)).Select(f => f._name).Distinct().ToList() ?? [];
        } catch (System.Exception) {
            return [];
        }
    }

    private static bool Usable(string? name) =>
        !string.IsNullOrEmpty(name) && name!.Length <= 40 &&
        name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '_' or '-' or '.');

    private static Font? FontOf(string name)
    {
        var list = SkinManager.Instance?.fontListOriginal;
        if (list == null) {
            return null;
        }

        var usable = list.Where(f => f != null && f.font != null && Usable(f._name)).ToList();
        return (usable.FirstOrDefault(f => f._name == name) ?? usable.FirstOrDefault())?.font;
    }

    internal static void Clear() => _cache.Clear();

    /// <summary>The layer's text as alpha (rows top-down), cut to its letters; null when it cannot be drawn here</summary>
    internal static (float[] Alpha, int W, int H)? Render(EmblemLayer layer)
    {
        var key = layer.Thing + "/" + layer.Font + "/" + layer.Spacing + "/" + layer.Curve;
        if (_cache.TryGetValue(key, out var known)) {
            return known;
        }

        (float[] Alpha, int W, int H)? result = null;
        try {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && layer.Text is { Length: > 0 } text &&
                FontOf(layer.Font) is { } font) {
                var strip = Strip(text, font, Mathf.Clamp(layer.Spacing, -8, 16));
                if (strip is { } s) {
                    var curve = Mathf.Clamp(layer.Curve, -180, 180);
                    result = Cut(curve == 0 ? s : Bend(s.Alpha, s.W, s.H, curve));
                }
            }
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Team emblem: the text {Text} could not be drawn", layer.Text);
        }

        if (_cache.Count >= Capacity) {
            _cache.Clear();
        }

        return _cache[key] = result;
    }

    /// <summary>The letters side by side on one line, top-down</summary>
    private static (float[] Alpha, int W, int H)? Strip(string text, Font font, int spacing)
    {
        font.RequestCharactersInTexture(text, Raster, FontStyle.Normal);
        var glyphs = new List<(CharacterInfo Info, int Pen, bool Has)>();
        var pen = 0;
        foreach (var c in text) {
            if (char.IsSurrogate(c)) {
                continue;
            }

            var has = font.GetCharacterInfo(c, out var info, Raster, FontStyle.Normal);
            glyphs.Add((info, pen, has && info.maxX > info.minX && info.maxY > info.minY));
            pen += (has ? info.advance : Raster / 3) + spacing;
        }

        var drawn = glyphs.Where(g => g.Has).ToList();
        if (drawn.Count == 0 || font.material?.mainTexture is not Texture2D atlasTexture) {
            return null;
        }

        var left = drawn.Min(g => g.Pen + g.Info.minX);
        var right = drawn.Max(g => g.Pen + g.Info.maxX);
        var top = drawn.Max(g => g.Info.maxY);
        var bottom = drawn.Min(g => g.Info.minY);
        int w = Mathf.Clamp(right - left, 1, MaxSide), h = Mathf.Clamp(top - bottom, 1, MaxSide);
        var (atlas, aw, ah) = ReadAlpha(atlasTexture);
        var alpha = new float[w * h];
        foreach (var (info, at, _) in drawn) {
            int gw = info.maxX - info.minX, gh = info.maxY - info.minY;
            int ox = at + info.minX - left, oy = top - info.maxY;
            for (var gy = 0; gy < gh; gy++) {
                var t = (gy + 0.5f) / gh;
                for (var gx = 0; gx < gw; gx++) {
                    int x = ox + gx, y = oy + gy;
                    if (x < 0 || y < 0 || x >= w || y >= h) {
                        continue;
                    }

                    // (the glyph's corners in the atlas: a glyph may be stored turned or flipped)
                    var s = (gx + 0.5f) / gw;
                    var uv = Vector2.Lerp(Vector2.Lerp(info.uvTopLeft, info.uvTopRight, s), Vector2.Lerp(info.uvBottomLeft, info.uvBottomRight, s), t);
                    var tx = Mathf.Clamp(Mathf.FloorToInt(uv.x * aw), 0, aw - 1);
                    var ty = Mathf.Clamp(Mathf.FloorToInt(uv.y * ah), 0, ah - 1);
                    var a = atlas[ty * aw + tx];
                    if (a > alpha[y * w + x]) {
                        alpha[y * w + x] = a;
                    }
                }
            }
        }

        return (alpha, w, h);
    }

    /// <summary>The font atlas' alpha (rows bottom-up as a texture holds them), copied through a render texture</summary>
    private static (float[] Alpha, int W, int H) ReadAlpha(Texture2D tex)
    {
        var (w, h) = (tex.width, tex.height);
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply();
            var px = copy.GetPixels32();
            Object.Destroy(copy);
            var alpha = new float[px.Length];
            for (var i = 0; i < px.Length; i++) {
                alpha[i] = px[i].a / 255f;
            }

            return (alpha, w, h);
        } finally {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    /// <summary>
    ///     The line bent along an arc of <paramref name="degrees" /> (its middle line keeps its length): above 0 an
    ///     arch (the circle's centre under the text), below 0 a smile
    /// </summary>
    private static (float[] Alpha, int W, int H) Bend(float[] a, int w, int h, int degrees)
    {
        var sign = degrees > 0 ? 1f : -1f;
        var radius = w / (Mathf.Abs(degrees) * Mathf.Deg2Rad);

        Vector2 Forward(float u, float v)
        {
            var along = (u - w / 2f) / radius;
            var r = radius + sign * (h / 2f - v);
            return new(r * Mathf.Sin(along), -sign * r * Mathf.Cos(along));
        }

        // the bent strip's bounds, from its edges
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var u = 0; u <= w; u++) {
            foreach (var v in new[] { 0f, h }) {
                var p = Forward(u, v);
                (minX, maxX, minY, maxY) = (Mathf.Min(minX, p.x), Mathf.Max(maxX, p.x), Mathf.Min(minY, p.y), Mathf.Max(maxY, p.y));
            }
        }

        var ow = Mathf.Clamp(Mathf.CeilToInt(maxX - minX) + 1, 1, MaxSide);
        var oh = Mathf.Clamp(Mathf.CeilToInt(maxY - minY) + 1, 1, MaxSide);
        var output = new float[ow * oh];
        for (var oy = 0; oy < oh; oy++) {
            for (var ox = 0; ox < ow; ox++) {
                float x = ox + 0.5f + minX, y = oy + 0.5f + minY;
                var r = Mathf.Sqrt(x * x + y * y);
                var along = Mathf.Atan2(x, -sign * y);
                var su = Mathf.FloorToInt(along * radius + w / 2f);
                var sv = Mathf.FloorToInt(h / 2f - sign * (r - radius));
                if (su >= 0 && su < w && sv >= 0 && sv < h) {
                    output[oy * ow + ox] = a[sv * w + su];
                }
            }
        }

        return (output, ow, oh);
    }

    /// <summary>The raster cut to its inked pixels</summary>
    private static (float[] Alpha, int W, int H)? Cut((float[] Alpha, int W, int H) r)
    {
        int x0 = r.W, y0 = r.H, x1 = -1, y1 = -1;
        for (var y = 0; y < r.H; y++) {
            for (var x = 0; x < r.W; x++) {
                if (r.Alpha[y * r.W + x] <= 0.1f) {
                    continue;
                }

                (x0, x1, y0, y1) = (Mathf.Min(x0, x), Mathf.Max(x1, x), Mathf.Min(y0, y), Mathf.Max(y1, y));
            }
        }

        if (x1 < 0) {
            return null;
        }

        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var cut = new float[w * h];
        for (var y = 0; y < h; y++) {
            System.Array.Copy(r.Alpha, (y0 + y) * r.W + x0, cut, y * w, w);
        }

        return (cut, w, h);
    }
}
