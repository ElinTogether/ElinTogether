using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ElinTogether.Helper;

/// <summary>
///     Draws a <see cref="Emblem" /> once into a 64x64 point-filtered texture (PLAN_team_emblem), kept by canonical
///     text (LRU of 256) so every view shares the sprite. The emblem's canvas is the 64 grid: a layer may reach past
///     the bottom shape (unless the emblem is cut to it) up to the canvas' edge, so a 16-24 px chip shows the same
///     square as before. An emblem an old form holds (<see cref="TeamEmblem.ToLegacy" />) is drawn by the old drawing,
///     pixel for pixel; any other by the layer drawing: each layer (and its live mirrored copies) from the cropped
///     sprite of its picture, its shape's formula or its text's raster (<see cref="EmblemText" />), nearest inverse
///     map (size, rotation, flips), tinted or filled (flat, gradient, tiled texture), cut to the layer below when
///     asked, its shadow and border, then blended (normal, multiply, add) over. The editor's previews are baked into
///     its own textures (<see cref="Into" />), never into the shared cache, so a team's chip never loses its sprite
/// </summary>
internal static class TeamEmblemBaker
{
    private const int N = TeamEmblem.Grid;
    private const int Capacity = 256;

    // a picture's pixels at most this many on their longer side (a portrait, a place's picture), and how many are kept
    private const int SourceSide = 256;
    private const int SourceCapacity = 128;

    private static readonly Dictionary<string, LinkedListNode<(string Key, Sprite Sprite)>> _baked = [];
    private static readonly LinkedList<(string Key, Sprite Sprite)> _order = new();

    // typed thing id -> its opaque pixels (the sprite cut to its alpha box), null when it cannot be read
    private static readonly Dictionary<string, (Color32[] Pixels, int W, int H)?> _sources = [];

    internal static Sprite Get(Emblem emblem)
    {
        var key = TeamEmblem.Format(emblem);
        if (_baked.TryGetValue(key, out var node) && node.Value.Sprite != null) {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Sprite;
        }

        if (node != null) {
            _order.Remove(node);
            _baked.Remove(key);
        }

        var sprite = Into(null, emblem);
        _baked[key] = _order.AddFirst((key, sprite));
        while (_order.Count > Capacity) {
            var last = _order.Last!.Value;
            _order.RemoveLast();
            _baked.Remove(last.Key);
            Free(last.Sprite);
        }

        return sprite;
    }

    /// <summary>
    ///     The emblem baked into <paramref name="reuse" />'s texture (rewritten in place: every image showing it
    ///     follows), or into a new sprite of its own when there is none. Not cached: the caller owns it
    /// </summary>
    internal static Sprite Into(Sprite? reuse, Emblem emblem)
    {
        var pixels = Bake(emblem);
        if (reuse != null && reuse.texture != null && reuse.texture.width == emblem.W && reuse.texture.height == emblem.H) {
            reuse.texture.SetPixels32(pixels);
            reuse.texture.Apply(false, false);
            return reuse;
        }

        var tex = new Texture2D(emblem.W, emblem.H, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "emp_emblem",
        };
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return Sprite.Create(tex, new Rect(0, 0, emblem.W, emblem.H), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    /// <summary>A sprite of <see cref="Into" /> destroyed with its texture</summary>
    internal static void Free(Sprite? sprite)
    {
        if (sprite == null) {
            return;
        }

        if (sprite.texture != null) {
            Object.Destroy(sprite.texture);
        }

        Object.Destroy(sprite);
    }

    /// <summary>A short hash of the baked pixels of a stored text (the bench compares it between games)</summary>
    internal static string PixelHash(Emblem emblem) => Hash(Get(emblem).texture.GetPixels32());

    /// <summary>The same hash of pixels baked now, without the cache (the bench checks an old text draws as before)</summary>
    internal static string Hash(Color32[] pixels)
    {
        unchecked {
            var h = 2166136261u;
            foreach (var c in pixels) {
                h = (h ^ c.r) * 16777619u;
                h = (h ^ c.g) * 16777619u;
                h = (h ^ c.b) * 16777619u;
                h = (h ^ c.a) * 16777619u;
            }

            return h.ToString("x8");
        }
    }

    internal static void Clear()
    {
        foreach (var (_, sprite) in _order) {
            Free(sprite);
        }

        _order.Clear();
        _baked.Clear();
        _sources.Clear();
        EmblemText.Clear();
    }

    /// <summary>The baked sprite as an image of <paramref name="size" /> in a layout row</summary>
    internal static Image Place(Transform parent, Sprite sprite, float size)
    {
        var go = new GameObject("Emblem", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        go.transform.SetParent(parent, false);
        // (also sized by hand: some rows do not control their children's height)
        ((RectTransform)go.transform).sizeDelta = new(size, size);
        var element = go.GetComponent<LayoutElement>();
        (element.minWidth, element.preferredWidth, element.minHeight, element.preferredHeight, element.flexibleWidth) = (size, size, size, size, 0f);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        return image;
    }

    /// <summary>The canvas' pixels (64x64 for an emblem), rows bottom-up as a texture holds them</summary>
    internal static Color32[] Bake(Emblem emblem) =>
        emblem.W == N && emblem.H == N && TeamEmblem.ToLegacy(emblem) is { } old ? BakeLegacy(old) : BakeLayers(emblem);

    // the canvas being baked by the layer drawing (a drawing's may be up to 256x256; a layer's own square stays N)
    private static int _w = N, _h = N;

    // ---------------------------------------------------------------- the layer drawing (e3)

    /// <summary>
    ///     Every field in its range again (review 2026-11-01: the bake is the second guard after Validate, an outline
    ///     of two billion pixels froze the game)
    /// </summary>
    private static EmblemLayer Safe(EmblemLayer l) => l with {
        X = Mathf.Clamp(l.X, -_w / 2, _w + _w / 2),
        Y = Mathf.Clamp(l.Y, -_h / 2, _h + _h / 2),
        Size = Mathf.Clamp(l.Size, TeamEmblem.MinSize, Mathf.Max(TeamEmblem.MaxSize, TeamEmblem.MaxSize * Mathf.Max(_w, _h) / N)),
        Rotation = ((l.Rotation % 360) + 360) % 360,
        Flip = l.Flip & 3,
        Opacity = Mathf.Clamp(l.Opacity, TeamEmblem.MinOpacity, 100),
        Outline = Mathf.Clamp(l.Outline, 0, TeamEmblem.MaxOutline),
        Pattern = Mathf.Clamp(l.Pattern, 0, TeamEmblem.PatternCount - 1),
        Sides = Mathf.Clamp(l.Sides, 3, 12),
        Aspect = Mathf.Clamp(l.Aspect, 10, 100),
        Thick = Mathf.Clamp(l.Thick, 5, 90),
        Spacing = Mathf.Clamp(l.Spacing, -8, 16),
        Curve = Mathf.Clamp(l.Curve, -180, 180),
        Align = Mathf.Clamp(l.Align, 0, 2),
        Shadow = Mathf.Clamp(l.Shadow, 0, TeamEmblem.MaxShadow),
        Gradient = Mathf.Clamp(l.Gradient, 0, 2),
        GradAngle = ((l.GradAngle % 360) + 360) % 360,
        Blend = Mathf.Clamp(l.Blend, 0, 2),
        Sym = l.Sym & 3,
    };

    /// <summary>The layer and its live mirrored copies (left-right, top-bottom, or both: four)</summary>
    private static IEnumerable<EmblemLayer> Copies(EmblemLayer l)
    {
        yield return l;
        var h = l with { X = _w - l.X, Flip = l.Flip ^ 1, Rotation = (360 - l.Rotation) % 360 };
        var v = l with { Y = _h - l.Y, Flip = l.Flip ^ 2, Rotation = (360 - l.Rotation) % 360 };
        if ((l.Sym & 1) != 0) {
            yield return h;
        }

        if ((l.Sym & 2) != 0) {
            yield return v;
        }

        if (l.Sym == 3) {
            yield return h with { Y = _h - l.Y, Flip = l.Flip ^ 3, Rotation = l.Rotation };
        }
    }

    private static Color32[] BakeLayers(Emblem emblem)
    {
        (_w, _h) = (Mathf.Clamp(emblem.W, 1, Drawing.MaxSide), Mathf.Clamp(emblem.H, 1, Drawing.MaxSide));
        // (top-down while drawing) a clear canvas, each layer over it
        var px = new Color[_w * _h];
        var cover = new float[_w * _h];
        var colour = new Color[_w * _h];
        var mask = new bool[_w * _h];
        bool[]? clip = null;
        bool[]? below = null;
        foreach (var (raw, index) in emblem.Layers.Take(TeamEmblem.MaxLayers).Select((l, i) => (l, i))) {
            var layer = Safe(raw);
            System.Array.Clear(cover, 0, cover.Length);
            System.Array.Clear(mask, 0, mask.Length);
            var drawn = false;
            foreach (var copy in Copies(layer)) {
                drawn |= copy.IsShape ? CoverShape(copy, cover, colour, mask) : copy.IsText ? CoverText(copy, cover, colour, mask) : CoverSprite(copy, cover, colour, mask);
            }

            var ring = drawn && layer.Outline > 0 ? Ring(mask, layer.Outline) : null;
            if (index == 0 && emblem.Clip) {
                // (what the bottom layer covers, its border included, is the emblem's outline; hidden or not)
                clip = new bool[_w * _h];
                for (var i = 0; i < clip.Length; i++) {
                    clip[i] = mask[i] || cover[i] > 0f || (ring != null && ring[i]);
                }
            }

            if (!drawn || layer.Hidden != 0) {
                continue;
            }

            if (layer.ClipBelow != 0 && below != null) {
                // (masked by the layer below: only where it is drawn)
                for (var i = 0; i < cover.Length; i++) {
                    if (!below[i]) {
                        cover[i] = 0f;
                        mask[i] = false;
                        if (ring != null) {
                            ring[i] = false;
                        }
                    }
                }
            }

            var opacity = layer.Opacity / 100f;
            var ink = layer.OutlineColor == TeamEmblem.Ink ? Ink : ToColor(layer.OutlineColor);
            if (layer.Shadow > 0) {
                // the shadow first: what the layer covers, moved down and right
                var shade = ToColor(layer.ShadowColor);
                var s = layer.Shadow;
                for (var y = s; y < _h; y++) {
                    for (var x = s; x < _w; x++) {
                        var j = (y - s) * _w + x - s;
                        var a = Mathf.Max(cover[j], ring != null && ring[j] ? 1f : 0f);
                        if (a > 0f && (layer.ClipBelow == 0 || below == null || below[y * _w + x])) {
                            Over(ref px[y * _w + x], shade, shade.a * a * opacity);
                        }
                    }
                }
            }

            var mine = new bool[_w * _h];
            for (var i = 0; i < px.Length; i++) {
                if (cover[i] > 0f) {
                    Over(ref px[i], Blend(px[i], colour[i], layer.Blend), cover[i] * opacity);
                    mine[i] = true;
                } else if (ring != null && ring[i]) {
                    Over(ref px[i], ink, ink.a * opacity);
                    mine[i] = true;
                }
            }

            below = mine;
        }

        var output = new Color32[_w * _h];
        for (var dy = 0; dy < _h; dy++) {
            for (var dx = 0; dx < _w; dx++) {
                var i = dy * _w + dx;
                var c = (Color32)px[i];
                if (clip != null && !clip[i]) {
                    c.a = 0;
                }

                output[(_h - 1 - dy) * _w + dx] = c;
            }
        }

        return output;
    }

    /// <summary>The colour a layer gives over a pixel: its own (normal), multiplied, or added (only where something is drawn)</summary>
    private static Color Blend(Color dst, Color src, int mode)
    {
        if (mode == 0 || dst.a <= 0f) {
            return src;
        }

        var mixed = mode == 1
            ? new Color(src.r * dst.r, src.g * dst.g, src.b * dst.b, src.a)
            : new Color(Mathf.Min(1f, src.r + dst.r), Mathf.Min(1f, src.g + dst.g), Mathf.Min(1f, src.b + dst.b), src.a);
        var c = Color.Lerp(src, mixed, dst.a);
        c.a = src.a;
        return c;
    }

    /// <summary>The colour over the pixel with that alpha (an opaque pixel blends as the old drawing did)</summary>
    private static void Over(ref Color dst, Color src, float a)
    {
        if (a <= 0f) {
            return;
        }

        if (dst.a >= 1f) {
            dst = Color.Lerp(dst, new(src.r, src.g, src.b, 1f), a);
            return;
        }

        var outA = a + dst.a * (1f - a);
        var keep = dst.a * (1f - a);
        dst = new((src.r * a + dst.r * keep) / outA, (src.g * a + dst.g * keep) / outA, (src.b * a + dst.b * keep) / outA, outA);
    }

    private static Color ToColor(uint rgba) =>
        new Color32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    /// <summary>
    ///     The fill at a point of the layer's own 64 square: flat, or the gradient from the fill to its end colour
    ///     (linear along its angle, 90 top to bottom; radial from the centre)
    /// </summary>
    private static Color FillAt(EmblemLayer l, Color fill, Color end, float lx, float ly)
    {
        if (l.Gradient == 0) {
            return fill;
        }

        float t;
        if (l.Gradient == 1) {
            var a = l.GradAngle * Mathf.Deg2Rad;
            t = ((lx - N / 2f) * Mathf.Cos(a) + (ly - N / 2f) * Mathf.Sin(a)) / N + 0.5f;
        } else {
            t = Mathf.Sqrt((lx - N / 2f) * (lx - N / 2f) + (ly - N / 2f) * (ly - N / 2f)) / (N / 2f);
        }

        return Color.Lerp(fill, end, Mathf.Clamp01(t));
    }

    /// <summary>
    ///     The inverse of the layer's place: the canvas pixel's centre in the layer's own 64 square (rotation undone,
    ///     then the flips, then the size)
    /// </summary>
    private readonly struct Inverse
    {
        private readonly float _cos, _sin, _scale;
        private readonly int _x, _y, _flip;

        internal Inverse(EmblemLayer layer, float scale)
        {
            float angle;
            if (layer.Rotation % 45 == 0) {
                // (eighths as the old drawing turned them, quarter turns exact)
                angle = layer.Rotation / 45 * Mathf.PI / 4f;
            } else {
                angle = layer.Rotation * Mathf.Deg2Rad;
            }

            (_cos, _sin) = (Mathf.Cos(angle), Mathf.Sin(angle));
            if (layer.Rotation % 90 == 0) {
                (_cos, _sin) = (Mathf.Round(_cos), Mathf.Round(_sin));
            }

            (_x, _y, _flip, _scale) = (layer.X, layer.Y, layer.Flip, scale);
        }

        /// <summary>Centred on the layer, in its own pixels</summary>
        internal Vector2 At(int dx, int dy) => At(dx + 0.5f, dy + 0.5f);

        /// <summary>A point of the canvas (not only a pixel's centre) in the layer's own pixels</summary>
        internal Vector2 At(float cx, float cy)
        {
            float x0 = cx - _x, y0 = cy - _y;
            var u = x0 * _cos + y0 * _sin;
            var v = -x0 * _sin + y0 * _cos;
            if ((_flip & 1) != 0) {
                u = -u;
            }

            if ((_flip & 2) != 0) {
                v = -v;
            }

            return new(u / _scale, v / _scale);
        }
    }

    // a picture this game does not have: a neutral grey tile with a darker edge
    private static readonly (Color32[] Pixels, int W, int H) Placeholder = MakePlaceholder();

    private static (Color32[] Pixels, int W, int H) MakePlaceholder()
    {
        const int n = 16;
        var px = new Color32[n * n];
        for (var y = 0; y < n; y++) {
            for (var x = 0; x < n; x++) {
                var edge = x == 0 || y == 0 || x == n - 1 || y == n - 1;
                px[y * n + x] = edge ? new Color32(70, 70, 70, 255) : new Color32(128, 128, 128, 255);
            }
        }

        return (px, n, n);
    }

    private static bool CoverSprite(EmblemLayer layer, float[] cover, Color[] colour, bool[] mask)
    {
        var src = Source(layer.Thing) ?? Placeholder;
        Color? tint = layer.Color != TeamEmblem.Own ? ToColor(layer.Color) : null;
        var scale = N * layer.Size / 100f / Mathf.Max(src.W, src.H, 1);
        var map = new Inverse(layer, scale);
        // a picture shrunk to a few canvas pixels (a portrait, a place: 100 to 256 pixels into 64) is averaged over
        // the source pixels each canvas pixel covers, up to 4x4 samples; nearest alone kept one pixel in four and the
        // portrait became noise (lead dev 2026-11-02). A picture drawn at its size or larger (the items, the icons)
        // keeps the nearest pixel, as before
        var k = Mathf.Clamp(Mathf.FloorToInt(1f / Mathf.Max(scale, 0.01f)), 1, 4);
        var weight = 1f / (k * k);
        for (var dy = 0; dy < _h; dy++) {
            for (var dx = 0; dx < _w; dx++) {
                Color sum = default;
                float alpha = 0f, solid = 0f;
                for (var sy = 0; sy < k; sy++) {
                    for (var sx = 0; sx < k; sx++) {
                        var p = k == 1 ? map.At(dx, dy) : map.At(dx + (sx + 0.5f) / k, dy + (sy + 0.5f) / k);
                        var px = Mathf.FloorToInt(p.x + src.W / 2f);
                        var pyTop = Mathf.FloorToInt(p.y + src.H / 2f);
                        if (px < 0 || px >= src.W || pyTop < 0 || pyTop >= src.H) {
                            continue;
                        }

                        var c = (Color)src.Pixels[(src.H - 1 - pyTop) * src.W + px];
                        if (c.a <= 0f) {
                            continue;
                        }

                        if (tint is { } t) {
                            var l = 0.3f + 0.7f * (0.299f * c.r + 0.587f * c.g + 0.114f * c.b);
                            c = new(t.r * l, t.g * l, t.b * l, c.a * t.a);
                        }

                        // (premultiplied, so a transparent sample darkens nothing)
                        sum += new Color(c.r * c.a, c.g * c.a, c.b * c.a, 0f);
                        alpha += c.a;
                        solid += c.a >= 0.5f ? 1f : 0f;
                    }
                }

                if (alpha <= 0f) {
                    continue;
                }

                var i = dy * _w + dx;
                var a = alpha * weight;
                cover[i] = a;
                colour[i] = new(sum.r / alpha, sum.g / alpha, sum.b / alpha, a);
                mask[i] = solid * weight >= 0.5f;
            }
        }

        return true;
    }

    /// <summary>
    ///     A text layer: its raster (<see cref="EmblemText" />) placed like a sprite (its longer side the layer's size,
    ///     its height squeezed by the aspect), anchored by its middle, left or right end, filled with its colour or
    ///     gradient. Nothing on a game that cannot draw text
    /// </summary>
    private static bool CoverText(EmblemLayer layer, float[] cover, Color[] colour, bool[] mask)
    {
        if (EmblemText.Render(layer) is not { } src) {
            return false;
        }

        var fill = ToColor(layer.Color);
        var end = ToColor(layer.GradColor);
        var aspect = layer.Aspect / 100f;
        var map = new Inverse(layer, N * layer.Size / 100f / Mathf.Max(src.W, src.H, 1));
        var anchor = layer.Align switch {
            1 => 0f,
            2 => src.W,
            _ => src.W / 2f,
        };
        for (var dy = 0; dy < _h; dy++) {
            for (var dx = 0; dx < _w; dx++) {
                var p = map.At(dx, dy);
                var sx = Mathf.FloorToInt(p.x + anchor);
                var syTop = Mathf.FloorToInt(p.y / aspect + src.H / 2f);
                if (sx < 0 || sx >= src.W || syTop < 0 || syTop >= src.H) {
                    continue;
                }

                var a = src.Alpha[syTop * src.W + sx];
                if (a <= 0f) {
                    continue;
                }

                var c = FillAt(layer, fill, end, (sx + 0.5f) * N / src.W, (syTop + 0.5f) * N / src.H);
                c.a *= a;
                var i = dy * _w + dx;
                cover[i] = c.a;
                colour[i] = c;
                mask[i] = a >= 0.5f;
            }
        }

        return true;
    }

    private static bool CoverShape(EmblemLayer layer, float[] cover, Color[] colour, bool[] mask)
    {
        var fill = ToColor(layer.Color);
        var second = ToColor(layer.Color2);
        var end = ToColor(layer.GradColor);
        var texture = layer.Texture.Length > 0 ? Source(layer.Texture) ?? Placeholder : ((Color32[] Pixels, int W, int H)?)null;
        var map = new Inverse(layer, layer.Size / 100f);
        for (var dy = 0; dy < _h; dy++) {
            for (var dx = 0; dx < _w; dx++) {
                var p = map.At(dx, dy);
                var (lx, ly) = (p.x + N / 2f, p.y + N / 2f);
                if (!Inside(layer, lx, ly)) {
                    continue;
                }

                var i = dy * _w + dx;
                Color c;
                if (layer.Pattern != 0 && Second(layer, lx, ly)) {
                    c = second;
                } else {
                    c = FillAt(layer, fill, end, lx, ly);
                    if (texture is { } t) {
                        // (the picture tiled at its own size across the shape, tinted by the fill: white keeps it)
                        var tx = ((Mathf.FloorToInt(lx) % t.W) + t.W) % t.W;
                        var ty = ((Mathf.FloorToInt(ly) % t.H) + t.H) % t.H;
                        var tc = (Color)t.Pixels[(t.H - 1 - ty) * t.W + tx];
                        c = new(tc.r * c.r, tc.g * c.g, tc.b * c.b, tc.a * c.a);
                    }
                }

                mask[i] = true;
                cover[i] = c.a;
                colour[i] = c;
            }
        }

        return true;
    }

    /// <summary>The bare pixels within <paramref name="width" /> of the covered ones (one: their four neighbours)</summary>
    private static bool[] Ring(bool[] mask, int width)
    {
        width = Mathf.Clamp(width, 0, TeamEmblem.MaxOutline);
        var ring = new bool[_w * _h];
        var r2 = width * width;
        for (var y = 0; y < _h; y++) {
            for (var x = 0; x < _w; x++) {
                if (mask[y * _w + x]) {
                    continue;
                }

                for (var oy = -width; oy <= width && !ring[y * _w + x]; oy++) {
                    for (var ox = -width; ox <= width; ox++) {
                        var (nx, ny) = (x + ox, y + oy);
                        if (ox * ox + oy * oy <= r2 && nx >= 0 && ny >= 0 && nx < _w && ny < _h && mask[ny * _w + nx]) {
                            ring[y * _w + x] = true;
                            break;
                        }
                    }
                }
            }
        }

        return ring;
    }

    /// <summary>
    ///     A point of the layer's own 64 square (top-down) is inside its shape. The old six keep their old formulas
    ///     (an aspect below 100 squeezes them upright); the others: a rounded square, a triangle pointed up, a star
    ///     and a polygon pointed up, a line across, a cross, a ring
    /// </summary>
    internal static bool Inside(EmblemLayer l, float lx, float ly)
    {
        if (lx < 0f || ly < 0f || lx >= N || ly >= N) {
            return false;
        }

        const float r = N / 2f;
        var asp = Mathf.Clamp(l.Aspect, 10, 100) / 100f;
        var cx = lx - r;
        var cy = ly - r;
        // (the shape squeezed to its height: the point stretched back into the full square)
        var sy = cy / asp;
        if (Mathf.Abs(sy) > r && l.Kind != "line") {
            return false;
        }

        var dx = Mathf.Abs(cx);
        var dy = Mathf.Abs(sy);
        var py = sy + r;
        var thick = Mathf.Clamp(l.Thick, 5, 90) / 100f;
        switch (l.Kind) {
            case "round":
                return dx * dx + dy * dy <= r * r;
            case "shield":
                if (py < N / 2f) {
                    return true;
                }

                var t = (py - N / 2f) / (N / 2f);
                return dx <= N / 2f * Mathf.Pow(Mathf.Max(0f, 1f - t), 0.6f);
            case "banner":
                const float notch = 16f;
                return dx <= r * 0.8f && !(py > N - notch && dx < py - (N - notch));
            case "diamond":
                return dx + dy <= r;
            case "hexagon":
                return dx <= r * 0.866f && dx * 0.57735f + dy <= r;
            case "rounded": {
                // (the corner a share of the shorter half side, not squeezed)
                var (hx, hy) = (r, r * asp);
                var rad = Mathf.Min(hx, hy) * thick;
                var qx = dx - (hx - rad);
                var qy = Mathf.Abs(cy) - (hy - rad);
                return qx <= 0f || qy <= 0f || qx * qx + qy * qy <= rad * rad;
            }
            case "triangle":
                return dx <= r * (py / N);
            case "star":
            case "polygon": {
                var n = Mathf.Clamp(l.Sides, 3, 12);
                var s = 2f * Mathf.PI / n;
                var rho = Mathf.Sqrt(dx * dx + dy * dy);
                var a = Mathf.Atan2(cx, -sy);
                if (a < 0f) {
                    a += 2f * Mathf.PI;
                }

                a %= s;
                if (l.Kind == "polygon") {
                    return rho * Mathf.Cos(a - s / 2f) <= r * Mathf.Cos(s / 2f);
                }

                if (a > s / 2f) {
                    a = s - a;
                }

                // inside the triangle of the centre, a point and the next inner corner
                var inner = r * thick;
                var (ex, ey) = (inner * Mathf.Cos(s / 2f) - r, inner * Mathf.Sin(s / 2f));
                var (px, pz) = (rho * Mathf.Cos(a) - r, rho * Mathf.Sin(a));
                return ex * pz - ey * px >= 0f;
            }
            case "line":
                return Mathf.Abs(cy) <= r * thick;
            case "cross":
                return dx <= r * thick || dy <= r * thick;
            case "ring": {
                var d2 = dx * dx + dy * dy;
                var inner = r * (1f - thick);
                return d2 <= r * r && d2 >= inner * inner;
            }
            default:
                // "square": the whole (squeezed) square
                return true;
        }
    }

    /// <summary>The point of the layer's square takes its pattern's second colour (the old patterns, any shape)</summary>
    private static bool Second(EmblemLayer l, float lx, float ly)
    {
        const int half = N / 2;
        var (x, y) = (Mathf.FloorToInt(lx), Mathf.FloorToInt(ly));
        switch (l.Pattern) {
            case 1:
                return x >= half;
            case 2:
                return y >= half;
            case 3:
                return x >= half ^ y >= half;
            case 4:
                return x > y;
            case 5:
                return x / 8 % 2 == 1;
            case 6:
                var top = N * 0.3f + Mathf.Abs(x + 0.5f - half) * 0.9f;
                return y + 0.5f >= top && y + 0.5f < top + 12f;
            case 7:
                const int w = 6;
                return !Inside(l, lx - w, ly) || !Inside(l, lx + w, ly) || !Inside(l, lx, ly - w) || !Inside(l, lx, ly + w);
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- the old drawing (e1, e2), unchanged

    private static Color32[] BakeLegacy(TeamEmblem.Legacy emblem)
    {
        // (top-down while drawing) the background: its pattern of the two colours
        var px = new Color[N * N];
        var bg = TeamBanner.ColorOf(emblem.Background);
        var bg2 = TeamBanner.ColorOf(emblem.Background2);
        for (var y = 0; y < N; y++) {
            for (var x = 0; x < N; x++) {
                px[y * N + x] = SecondLegacy(emblem.Pattern, emblem.Shape, x, y) ? bg2 : bg;
            }
        }

        var cover = new float[N * N];
        var colour = new Color[N * N];
        foreach (var layer in emblem.Layers) {
            // (a thing this game lacks: the placeholder, review 2026-11-01)
            var src = Source(layer.Thing) ?? Placeholder;

            System.Array.Clear(cover, 0, cover.Length);
            Color? tint = layer.Tint < TeamEmblem.OwnColours ? TeamBanner.ColorOf(layer.Tint) : null;
            var opacity = Mathf.Clamp(layer.Opacity, TeamEmblem.MinOpacity, 100) / 100f;
            var scale = N * layer.Size / 100f / Mathf.Max(src.W, src.H, 1);
            // undo the clockwise turn (eighths of a turn), then the flips
            var angle = layer.Rotation8 * Mathf.PI / 4f;
            var (cos, sin) = (Mathf.Cos(angle), Mathf.Sin(angle));
            if (layer.Rotation8 % 2 == 0) {
                // (quarter turns stay exact: the same pixels as the first form drew)
                (cos, sin) = (Mathf.Round(cos), Mathf.Round(sin));
            }

            for (var dy = 0; dy < N; dy++) {
                for (var dx = 0; dx < N; dx++) {
                    float x0 = dx + 0.5f - layer.X, y0 = dy + 0.5f - layer.Y;
                    var u = x0 * cos + y0 * sin;
                    var v = -x0 * sin + y0 * cos;

                    if ((layer.Flip & 1) != 0) {
                        u = -u;
                    }

                    if ((layer.Flip & 2) != 0) {
                        v = -v;
                    }

                    var sx = Mathf.FloorToInt(u / scale + src.W / 2f);
                    var syTop = Mathf.FloorToInt(v / scale + src.H / 2f);
                    if (sx < 0 || sx >= src.W || syTop < 0 || syTop >= src.H) {
                        continue;
                    }

                    var c = (Color)src.Pixels[(src.H - 1 - syTop) * src.W + sx];
                    if (c.a <= 0f) {
                        continue;
                    }

                    if (tint is { } t) {
                        var l = 0.3f + 0.7f * (0.299f * c.r + 0.587f * c.g + 0.114f * c.b);
                        c = new(t.r * l, t.g * l, t.b * l, c.a);
                    }

                    var i = dy * N + dx;
                    cover[i] = c.a;
                    colour[i] = c;
                }
            }

            // the outline first, on the pixels the layer leaves bare beside the ones it covers; then the layer
            for (var i = 0; i < px.Length; i++) {
                if (cover[i] > 0f) {
                    px[i] = Color.Lerp(px[i], new(colour[i].r, colour[i].g, colour[i].b, 1f), cover[i] * opacity);
                } else if (layer.Outline != 0 && Borders(cover, i)) {
                    px[i] = Color.Lerp(px[i], Ink, opacity);
                }
            }
        }

        var output = new Color32[N * N];
        for (var dy = 0; dy < N; dy++) {
            for (var dx = 0; dx < N; dx++) {
                var c = (Color32)px[dy * N + dx];
                c.a = InsideLegacy(emblem.Shape, dx, dy) ? (byte)255 : (byte)0;
                output[(N - 1 - dy) * N + dx] = c;
            }
        }

        return output;
    }

    // the outline's dark ink
    private static readonly Color Ink = new(0.1f, 0.08f, 0.06f, 1f);

    /// <summary>A bare pixel next to one the layer covers at least half (its four neighbours)</summary>
    private static bool Borders(float[] cover, int i)
    {
        var (x, y) = (i % N, i / N);
        return (x > 0 && cover[i - 1] >= 0.5f) || (x < N - 1 && cover[i + 1] >= 0.5f) ||
               (y > 0 && cover[i - N] >= 0.5f) || (y < N - 1 && cover[i + N] >= 0.5f);
    }

    /// <summary>
    ///     The pixel (top-down) takes the pattern's second colour: halves, quarters, a diagonal, stripes, a chevron, or a
    ///     border along the shape's edge
    /// </summary>
    private static bool SecondLegacy(int pattern, int shape, int x, int y)
    {
        const int half = N / 2;
        switch (pattern) {
            case 1:
                return x >= half;
            case 2:
                return y >= half;
            case 3:
                return x >= half ^ y >= half;
            case 4:
                return x > y;
            case 5:
                return x / 8 % 2 == 1;
            case 6:
                // an upturned V across the middle, 12 pixels thick
                var top = N * 0.3f + Mathf.Abs(x + 0.5f - half) * 0.9f;
                return y + 0.5f >= top && y + 0.5f < top + 12f;
            case 7:
                const int w = 6;
                return !InsideLegacy(shape, x - w, y) || !InsideLegacy(shape, x + w, y) || !InsideLegacy(shape, x, y - w) || !InsideLegacy(shape, x, y + w);
            default:
                return false;
        }
    }

    /// <summary>
    ///     The pixel (top-down) is inside the shape: square, round, a shield pointed at the bottom, a banner with a
    ///     swallowtail, a diamond, a hexagon
    /// </summary>
    private static bool InsideLegacy(int shape, int x, int y)
    {
        if (x < 0 || y < 0 || x >= N || y >= N) {
            return false;
        }

        var dx = Mathf.Abs(x + 0.5f - N / 2f);
        var py = y + 0.5f;
        var dy = Mathf.Abs(py - N / 2f);
        const float r = N / 2f;
        switch (shape) {
            case 1:
                return dx * dx + dy * dy <= r * r;
            case 2:
                if (py < N / 2f) {
                    return true;
                }

                var t = (py - N / 2f) / (N / 2f);
                return dx <= N / 2f * Mathf.Pow(Mathf.Max(0f, 1f - t), 0.6f);
            case 3:
                // a hanging banner, a little narrower than the square, its bottom cut in a V
                const float notch = 16f;
                return dx <= r * 0.8f && !(py > N - notch && dx < py - (N - notch));
            case 4:
                return dx + dy <= r;
            case 5:
                // pointed at the top and the bottom, its sides upright
                return dx <= r * 0.866f && dx * 0.57735f + dy <= r;
            default:
                return true;
        }
    }

    private static (Color32[] Pixels, int W, int H)? Source(string thing)
    {
        if (_sources.TryGetValue(thing, out var known)) {
            return known;
        }

        (Color32[] Pixels, int W, int H)? result = null;
        try {
            if (EmblemPaint.Is(thing)) {
                // (a paint layer keeps its whole 64 square: never cut to its drawn part)
                result = EmblemPaint.Source(thing);
            } else if (TeamBanner.SpriteOf(thing) is { } sprite) {
                var src = Patches.MultiplayerHubPatch.Read(sprite);
                var box = Patches.MultiplayerHubPatch.AlphaBox(src.Pixels, src.W, src.H);
                var cut = new Color32[box.width * box.height];
                for (var y = 0; y < box.height; y++) {
                    System.Array.Copy(src.Pixels, (box.y + y) * src.W + box.x, cut, y * box.width, box.width);
                }

                // (a portrait or a place's picture kept at most 256 pixels: the 64 grid at 200 % needs 128)
                result = EmblemSprites.Fit(cut, box.width, box.height, SourceSide);
            }
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Team emblem: the sprite of {Thing} could not be read", thing);
        }

        if (_sources.Count > SourceCapacity) {
            _sources.Clear();
        }

        return _sources[thing] = result;
    }
}
