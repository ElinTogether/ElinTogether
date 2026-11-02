using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using UnityEngine;

namespace ElinTogether.Components;

/// <summary>
///     The editor's paint tools (lead dev 2026-11-02: "think of paint.net"): move, rectangle select, pencil, brush,
///     eraser, fill, line, rectangle, ellipse, the other shapes (star, triangle, shield...: lead dev 2026-11-02, a tool
///     dragged out on the canvas like the rectangle), colour picker, text; a primary and a secondary colour (left and
///     right button). Shift keeps a shape's sides equal and a line straight or diagonal. The brush and the eraser have a size (1 to 32), a hardness (a soft edge) and an opacity; the shapes and
///     the bucket an opacity. A selection keeps every paint tool inside it; its pixels move with the select tool, and
///     copy, cut, paste and delete work on them. The paint tools draw into the selected paint layer
///     (<see cref="EmblemPaint" />); on another layer, a first stroke adds a paint layer over it. A stroke is one undo
///     step
/// </summary>
internal static partial class EmblemEditor
{
    internal const int ToolMove = 0, ToolPencil = 1, ToolBrush = 2, ToolEraser = 3, ToolFill = 4, ToolLine = 5, ToolRect = 6,
        ToolEllipse = 7, ToolPicker = 8, ToolText = 9, ToolSelect = 10, ToolShape = 11;

    // the tool column's order and each tool's key (paint.net's, with the lead dev's G for the bucket and I for the picker)
    private static readonly int[] ToolOrder = [
        ToolMove, ToolSelect, ToolPencil, ToolBrush, ToolEraser, ToolFill, ToolLine, ToolRect, ToolEllipse, ToolShape, ToolPicker, ToolText,
    ];

    private static readonly Dictionary<int, KeyCode> ToolKeys = new() {
        [ToolMove] = KeyCode.V, [ToolSelect] = KeyCode.M, [ToolPencil] = KeyCode.P, [ToolBrush] = KeyCode.B, [ToolEraser] = KeyCode.E,
        [ToolFill] = KeyCode.G, [ToolLine] = KeyCode.L, [ToolRect] = KeyCode.R, [ToolEllipse] = KeyCode.O, [ToolPicker] = KeyCode.I,
        [ToolText] = KeyCode.T, [ToolShape] = KeyCode.U,
    };

    private static string ToolTip(int tool) => tool == ToolSelect ? "emp_ui_editor_tool_select" : tool == ToolShape ? "emp_ui_editor_tool_shape" : new[] {
        "emp_ui_emblem_tool_move", "emp_ui_emblem_tool_pencil", "emp_ui_emblem_tool_brush", "emp_ui_emblem_tool_eraser",
        "emp_ui_emblem_tool_fill", "emp_ui_emblem_tool_line", "emp_ui_emblem_tool_rect", "emp_ui_emblem_tool_ellipse",
        "emp_ui_emblem_tool_picker", "emp_ui_emblem_tool_text",
    }[tool];

    internal const int MaxBrush = 32;

    // the colour fields of the pair (beside the layer's own, NoColour..GradColour)
    private const int Primary = 10, Secondary = 11;

    private static int _tool = ToolBrush;
    private static uint _primary = 0x1A140FFFu;
    private static uint _secondary = 0xFFFFFFFFu;
    private static int _brush = 3;
    private static int _hardness = 100;
    private static int _opacity = 100;
    private static bool _filled;

    // the shape tool's shape (one of the shape layers' kinds) and a star's points or a polygon's sides
    internal static readonly string[] ToolShapes = ["star", "triangle", "diamond", "hexagon", "polygon", "shield", "banner", "rounded", "cross"];
    private static string _shapeKind = "star";
    private static int _shapeSides = 5;

    // mirror painting: 0 off, 1 left-right, 2 top-bottom, 3 both (each paint tool draws its mirrored copies too)
    private static int _mirror;
    private static bool _grid = true;

    // the stroke being drawn: its colour, the layer's pixels when it began (lines and shapes are drawn again over
    // them), how much of the ink each pixel got (a soft or see-through stroke never darkens where it passes twice),
    // where it began and where the pointer last was, in the layer's pixels
    private static bool _painting;
    private static bool _erasing;
    private static Color32 _ink;
    private static Color32[]? _canvas;
    private static Color32[]? _strokeBase;
    private static float[]? _cover;
    private static Vector2Int _from;
    private static Vector2Int _last;

    // a selection keeps the stroke inside it (in the layer's pixels while a stroke is drawn)
    private static RectInt? _strokeClip;

    // the last commit had more than 255 colours: they were rounded (said in the status line)
    private static bool _reduced;

    // the paint layer's size in pixels while a stroke is drawn
    private static int _pw = EmblemPaint.N, _ph = EmblemPaint.N;

    private static Texture2D? _gridTexture;
    private static int _gridZoom;

    private static bool Paints(int tool) => tool is ToolPencil or ToolBrush or ToolEraser or ToolFill or ToolLine or ToolRect or ToolEllipse or ToolShape;

    private static bool Sized(int tool) => tool is ToolBrush or ToolEraser or ToolLine or ToolRect or ToolEllipse or ToolShape;

    private static bool Soft(int tool) => tool is ToolBrush or ToolEraser;

    private static bool SeeThrough(int tool) => tool is ToolBrush or ToolEraser or ToolFill or ToolLine or ToolRect or ToolEllipse or ToolShape;

    // the tools dragged out from where they began (Shift keeps them square or straight)
    private static bool Dragged(int tool) => tool is ToolLine or ToolRect or ToolEllipse or ToolShape;

    // ---------------------------------------------------------------- textures of the canvas

    private static Texture2D? _checker;

    /// <summary>Two by two squares of light parchment, tiled under the canvas (one square: 8 of its pixels)</summary>
    private static Texture2D Checker()
    {
        if (_checker != null) {
            return _checker;
        }

        _checker = new Texture2D(2, 2, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat,
            name = "emp_emblem_checker",
        };
        var a = new Color32(240, 232, 214, 255);
        var b = new Color32(226, 215, 192, 255);
        _checker.SetPixels32([a, b, b, a]);
        _checker.Apply(false, false);
        return _checker;
    }

    /// <summary>One cell of the pixel grid at that zoom (a faint line on its top and left), tiled over the canvas</summary>
    private static Texture2D GridTexture(int zoom)
    {
        if (_gridTexture != null && _gridZoom == zoom) {
            return _gridTexture;
        }

        if (_gridTexture != null) {
            Object.Destroy(_gridTexture);
        }

        var tex = new Texture2D(zoom, zoom, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat,
            name = "emp_emblem_grid",
        };
        var px = new Color32[zoom * zoom];
        for (var y = 0; y < zoom; y++) {
            for (var x = 0; x < zoom; x++) {
                px[y * zoom + x] = x == 0 || y == zoom - 1 ? new Color32(40, 32, 24, 46) : new Color32(0, 0, 0, 0);
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, false);
        _gridZoom = zoom;
        return _gridTexture = tex;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>A point of the canvas in a paint layer's own pixels (its place, size, turn and flips undone)</summary>
    private static Vector2 LayerPoint(EmblemLayer l, Vector2 p, int pw, int ph)
    {
        float x0 = p.x - l.X, y0 = p.y - l.Y;
        var angle = l.Rotation * Mathf.Deg2Rad;
        var (cos, sin) = (Mathf.Cos(angle), Mathf.Sin(angle));
        var u = x0 * cos + y0 * sin;
        var v = -x0 * sin + y0 * cos;
        if ((l.Flip & 1) != 0) {
            u = -u;
        }

        if ((l.Flip & 2) != 0) {
            v = -v;
        }

        // (as the baker places a picture: its longer side the layer's size in % of 64 pixels)
        var scale = Mathf.Max(0.01f, EmblemPaint.N * l.Size / 100f / Mathf.Max(pw, ph, 1));
        return new(u / scale + pw / 2f, v / scale + ph / 2f);
    }

    private static Vector2Int LayerPixel(EmblemLayer l, Vector2 p, int pw, int ph)
    {
        var at = LayerPoint(l, p, pw, ph);
        return new(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));
    }

    /// <summary>A rectangle of the canvas (the selection) in a paint layer's pixels, clamped to it</summary>
    private static RectInt LayerRect(EmblemLayer l, RectInt r, int pw, int ph)
    {
        var a = LayerPoint(l, new(r.xMin + 0.5f, r.yMin + 0.5f), pw, ph);
        var b = LayerPoint(l, new(r.xMax - 0.5f, r.yMax - 0.5f), pw, ph);
        int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, b.x)), 0, pw), y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, b.y)), 0, ph);
        int x1 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(a.x, b.x)) + 1, 0, pw), y1 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(a.y, b.y)) + 1, 0, ph);
        return new(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
    }

    private static Color32 ToColor32(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    private static uint ToRgba(Color32 c) => ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;

    /// <summary>The selected layer as a paint layer to draw on: itself, or a new one over the selection</summary>
    private static bool PaintTarget()
    {
        if (HasSelection && Current.IsPaint && Current.Lock == 0 && Current.Hidden == 0) {
            return true;
        }

        if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
            _note = "emp_ui_editor_layers_full".lang();
            return false;
        }

        AddPaintLayer(false);
        return HasSelection && Current.IsPaint;
    }

    /// <summary>A paint tool pressed on the canvas: the selected paint layer, or a new one over the selection</summary>
    private static void PaintDown(Vector2 p, bool right)
    {
        // (Alt with a paint tool picks the colour under the pointer, as paint.net's Ctrl does)
        if (_tool == ToolPicker || Paints(_tool) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) {
            PickColour(p, right);
            return;
        }

        if (_tool == ToolText) {
            if (_draft.Layers.Count < TeamEmblem.MaxLayers) {
                AddText();
                Edit(l => l with { X = PosX(p.x), Y = PosY(p.y) });
                Fill();
            }

            return;
        }

        var before = _draft.Layers.Count;
        if (!PaintTarget() || EmblemPaint.Decode(Current.Thing) is not { } decoded) {
            return;
        }

        var added = _draft.Layers.Count != before;
        (_canvas, _pw, _ph) = decoded;
        _strokeBase = (Color32[])_canvas.Clone();
        _cover = new float[_canvas.Length];
        _erasing = _tool == ToolEraser;
        var colour = right ? _secondary : _primary;
        _ink = _erasing ? new Color32(0, 0, 0, 0) : ToColor32(colour);
        if (!_erasing) {
            ColourPicker.Remember(colour);
        }

        _strokeClip = _sel is { } sel ? LayerRect(Current, sel, _pw, _ph) : null;
        _painting = true;
        _from = _last = LayerPixel(Current, p, _pw, _ph);
        // (a stroke that added its layer is one undo step with it)
        _dragKey = added ? "paint" : null;
        // (the list, the history and Save shown again when the pointer is released)
        _reselect = true;
        if (_tool == ToolFill) {
            foreach (var (fx, fy) in Mirrors()) {
                Flood(_canvas, new(fx ? _pw - 1 - _from.x : _from.x, fy ? _ph - 1 - _from.y : _from.y));
            }

            Commit();
            PaintUp();
            return;
        }

        Stroke(_from);
        Commit();
    }

    private static void PaintDrag(Vector2 p)
    {
        if (!_painting || _canvas == null || !HasSelection) {
            return;
        }

        var at = LayerPixel(Current, p, _pw, _ph);
        if (at == _last) {
            return;
        }

        Stroke(at);
        _last = at;
        Commit();
    }

    private static void PaintUp()
    {
        _painting = false;
        _canvas = null;
        _strokeBase = null;
        _cover = null;
        _strokeClip = null;
        _dragKey = null;
        BakeNow();
    }

    /// <summary>The stroke drawn to that point: a free line from the last point, or the line or shape from its start</summary>
    private static void Stroke(Vector2Int at)
    {
        if (_canvas == null || _strokeBase == null || _cover == null) {
            return;
        }

        var width = _tool == ToolPencil ? 1 : _brush;
        if (Dragged(_tool) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || _benchShift)) {
            at = Constrained(_from, at, _tool == ToolLine);
        }

        // (a line or a shape is drawn again from the stroke's start at each move)
        if (Dragged(_tool)) {
            Restart();
        }

        foreach (var (fx, fy) in Mirrors()) {
            // a point mirrored across the layer's middle; a dab of two pixels grows right and down, its copy moved back
            // one so the two are each other's mirror
            Vector2Int M(Vector2Int v, bool dab = false)
            {
                var back = dab && width == 2 ? 1 : 0;
                return new(fx ? _pw - 1 - v.x - back : v.x, fy ? _ph - 1 - v.y - back : v.y);
            }

            switch (_tool) {
                case ToolLine:
                    Line(_canvas, M(_from, true), M(at, true), width);
                    break;
                case ToolRect:
                case ToolEllipse:
                    Shape(_canvas, M(_from), M(at), width, _tool == ToolEllipse, _filled);
                    break;
                case ToolShape:
                    KindShape(_canvas, M(_from), M(at), width, _filled, fx, fy);
                    break;
                default:
                    Line(_canvas, M(_last, true), M(at, true), width);
                    break;
            }
        }
    }

    /// <summary>The copies mirror painting draws: the stroke itself, then its mirrored ones (across, down, both)</summary>
    private static IEnumerable<(bool X, bool Y)> Mirrors()
    {
        yield return (false, false);
        if ((_mirror & 1) != 0) {
            yield return (true, false);
        }

        if ((_mirror & 2) != 0) {
            yield return (false, true);
        }

        if (_mirror == 3) {
            yield return (true, true);
        }
    }

    // a line or a shape is drawn again from the stroke's start at each move
    private static void Restart()
    {
        System.Array.Copy(_strokeBase!, _canvas!, _canvas!.Length);
        System.Array.Clear(_cover!, 0, _cover!.Length);
    }

    /// <summary>The canvas into the selected layer (one undo step for the whole stroke); its colours rounded past 255</summary>
    private static void Commit()
    {
        if (_canvas == null) {
            return;
        }

        var code = EmblemPaint.Encode(_canvas, _pw, _ph);
        _reduced = code == null;
        code ??= EmblemPaint.EncodeReduced(_canvas, _pw, _ph);
        if (code == null) {
            return;
        }

        Edit(l => l with { Thing = code }, "paint");
    }

    /// <summary>
    ///     How much of the ink a pixel gets: a tool's opacity, the brush's soft edge. A pixel keeps the most it got in
    ///     this stroke (passing twice never darkens it)
    /// </summary>
    private static void Put(Color32[] px, int x, int y, float amount)
    {
        if (x < 0 || y < 0 || x >= _pw || y >= _ph || amount <= 0f || _strokeClip is { } clip && !clip.Contains(new(x, y))) {
            return;
        }

        var i = y * _pw + x;
        if (_cover != null) {
            if (amount <= _cover[i]) {
                return;
            }

            _cover[i] = amount;
        }

        var under = _strokeBase != null ? _strokeBase[i] : px[i];
        px[i] = Mix(under, _ink, amount, _erasing);
    }

    /// <summary>
    ///     The ink over a pixel: all of it replaces the pixel (the pixel art's exact colour); a part of it is laid over
    ///     it (an eraser takes that part of its alpha away). Rounded so a soft stroke keeps few colours
    /// </summary>
    internal static Color32 Mix(Color32 under, Color32 ink, float amount, bool erase)
    {
        if (amount >= 0.999f) {
            return erase ? default : ink;
        }

        if (erase) {
            var a = Mathf.RoundToInt(under.a * (1f - amount) / 8f) * 8;
            return a <= 0 ? default : new(under.r, under.g, under.b, (byte)Mathf.Min(255, a));
        }

        var src = ink.a / 255f * amount;
        var dst = under.a / 255f * (1f - src);
        var outA = src + dst;
        if (outA <= 0.001f) {
            return default;
        }

        byte Channel(byte i, byte u) => (byte)Mathf.Clamp(Mathf.RoundToInt((i * src + u * dst) / outA / 4f) * 4, 0, 255);
        return new(Channel(ink.r, under.r), Channel(ink.g, under.g), Channel(ink.b, under.b), (byte)Mathf.Clamp(Mathf.RoundToInt(outA * 255f / 8f) * 8, 8, 255));
    }

    private static float Strength => SeeThrough(_tool) ? _opacity / 100f : 1f;

    private static void Dot(Color32[] px, int cx, int cy, int width)
    {
        var strength = Strength;
        // (one pixel, or a square of two: the pixel art's own dabs)
        if (width <= 2) {
            for (var y = cy; y < cy + width; y++) {
                for (var x = cx; x < cx + width; x++) {
                    Put(px, x, y, strength);
                }
            }

            return;
        }

        // a round dab: hard (as before), or fading from the hardness' share of its radius to its edge
        var r = (width - 1) / 2f;
        var edge = r + 0.5f;
        var inner = edge * (Soft(_tool) ? _hardness / 100f : 1f);
        for (var y = Mathf.FloorToInt(cy - r); y <= Mathf.CeilToInt(cy + r); y++) {
            for (var x = Mathf.FloorToInt(cx - r); x <= Mathf.CeilToInt(cx + r); x++) {
                var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                float amount;
                if (!Soft(_tool) || _hardness >= 100) {
                    amount = d2 <= r * r + 0.5f ? 1f : 0f;
                } else {
                    var d = Mathf.Sqrt(d2);
                    amount = d <= inner ? 1f : Mathf.Clamp01((edge - d) / Mathf.Max(0.5f, edge - inner));
                }

                Put(px, x, y, amount * strength);
            }
        }
    }

    /// <summary>A line of dabs from a to b (Bresenham)</summary>
    private static void Line(Color32[] px, Vector2Int a, Vector2Int b, int width)
    {
        int x = a.x, y = a.y, dx = Mathf.Abs(b.x - a.x), dy = -Mathf.Abs(b.y - a.y);
        int sx = a.x < b.x ? 1 : -1, sy = a.y < b.y ? 1 : -1, err = dx + dy;
        for (var guard = 0; guard < 4 * Drawing.MaxSide * Drawing.MaxSide; guard++) {
            Dot(px, x, y, width);
            if (x == b.x && y == b.y) {
                break;
            }

            var e2 = 2 * err;
            if (e2 >= dy) {
                err += dy;
                x += sx;
            }

            if (e2 <= dx) {
                err += dx;
                y += sy;
            }
        }
    }

    /// <summary>A rectangle or an ellipse in the box from a to b, its outline of that width or filled</summary>
    private static void Shape(Color32[] px, Vector2Int a, Vector2Int b, int width, bool ellipse, bool filled)
    {
        var strength = Strength;
        int x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
        float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f, rx = (x1 - x0) / 2f + 0.5f, ry = (y1 - y0) / 2f + 0.5f;
        for (var y = Mathf.Max(0, y0); y <= Mathf.Min(_ph - 1, y1); y++) {
            for (var x = Mathf.Max(0, x0); x <= Mathf.Min(_pw - 1, x1); x++) {
                bool inside, edge;
                if (ellipse) {
                    float nx = (x - cx) / rx, ny = (y - cy) / ry;
                    inside = nx * nx + ny * ny <= 1f;
                    float ix = (x - cx) / Mathf.Max(0.5f, rx - width), iy = (y - cy) / Mathf.Max(0.5f, ry - width);
                    edge = inside && (rx <= width || ry <= width || ix * ix + iy * iy > 1f);
                } else {
                    inside = true;
                    edge = x < x0 + width || x > x1 - width || y < y0 + width || y > y1 - width;
                }

                if (inside && (filled || edge)) {
                    Put(px, x, y, strength);
                }
            }
        }
    }

    // the bench holds Shift down
    private static bool _benchShift;

    /// <summary>
    ///     Shift held: a line's end on the nearest of the eight directions from its start; a shape's box made square
    ///     (as long as its longer side)
    /// </summary>
    internal static Vector2Int Constrained(Vector2Int from, Vector2Int at, bool line)
    {
        var d = at - from;
        int ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y);
        if (line) {
            // (within about 22 degrees of an axis: along it; else the diagonal)
            if (ay * 5 < ax * 2) {
                return new(at.x, from.y);
            }

            if (ax * 5 < ay * 2) {
                return new(from.x, at.y);
            }
        }

        var side = Mathf.Max(ax, ay);
        return new(from.x + (d.x < 0 ? -side : side), from.y + (d.y < 0 ? -side : side));
    }

    /// <summary>
    ///     The shape tool's shape stretched over the box from a to b (as a shape layer draws its 64 square,
    ///     <see cref="TeamEmblemBaker.Inside" />): filled, or its outline of that width (the pixels inside within width of
    ///     its edge)
    /// </summary>
    private static void KindShape(Color32[] px, Vector2Int a, Vector2Int b, int width, bool filled, bool flipX = false, bool flipY = false)
    {
        var strength = Strength;
        int x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var mask = ShapeMask(_shapeKind, _shapeSides, w, h);
        var edge = filled ? null : Edge(mask, w, h, width);
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++) {
                // (a mirrored copy: the shape itself turned over, a triangle pointing down under one pointing up)
                var i = (flipY ? h - 1 - y : y) * w + (flipX ? w - 1 - x : x);
                if (mask[i] && (edge == null || edge[i])) {
                    Put(px, x0 + x, y0 + y, strength);
                }
            }
        }
    }

    /// <summary>Which pixels of a w by h box the shape covers (rows top-down)</summary>
    internal static bool[] ShapeMask(string kind, int sides, int w, int h)
    {
        var layer = TeamEmblem.Shape(kind, 0) with { Sides = Mathf.Clamp(sides, 3, 12) };
        var mask = new bool[w * h];
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++) {
                // (the pixel's middle in the shape's own 64 square)
                mask[y * w + x] = TeamEmblemBaker.Inside(layer, (x + 0.5f) * TeamEmblem.Grid / w, (y + 0.5f) * TeamEmblem.Grid / h);
            }
        }

        return mask;
    }

    /// <summary>
    ///     The covered pixels within <paramref name="width" /> of an uncovered one or of the box's side (the mask worn
    ///     away that many times, four neighbours each time)
    /// </summary>
    internal static bool[] Edge(bool[] mask, int w, int h, int width)
    {
        var inner = (bool[])mask.Clone();
        for (var pass = 0; pass < Mathf.Max(1, width); pass++) {
            var next = new bool[inner.Length];
            for (var y = 1; y < h - 1; y++) {
                for (var x = 1; x < w - 1; x++) {
                    var i = y * w + x;
                    next[i] = inner[i] && inner[i - 1] && inner[i + 1] && inner[i - w] && inner[i + w];
                }
            }

            inner = next;
        }

        var edge = new bool[mask.Length];
        for (var i = 0; i < mask.Length; i++) {
            edge[i] = mask[i] && !inner[i];
        }

        return edge;
    }

    /// <summary>The bucket: the area of the clicked pixel's colour (4-connected, inside the selection) in the ink</summary>
    private static void Flood(Color32[] px, Vector2Int at)
    {
        int n = _pw, rows = _ph;
        if (at.x < 0 || at.y < 0 || at.x >= n || at.y >= rows || _strokeClip is { } clip && !clip.Contains(at)) {
            return;
        }

        var start = (Color32[])px.Clone();
        var target = start[at.y * n + at.x];
        bool Same(Color32 c) => c.a == 0 && target.a == 0 || c.r == target.r && c.g == target.g && c.b == target.b && c.a == target.a;
        if (Strength >= 0.999f && Same(_ink)) {
            return;
        }

        var seen = new bool[start.Length];
        var stack = new Stack<int>();
        stack.Push(at.y * n + at.x);
        var strength = Strength;
        while (stack.Count > 0) {
            var i = stack.Pop();
            if (seen[i] || !Same(start[i])) {
                continue;
            }

            seen[i] = true;
            int x = i % n, y = i / n;
            if (_strokeClip is { } k && !k.Contains(new(x, y))) {
                continue;
            }

            Put(px, x, y, strength);
            if (x > 0) stack.Push(i - 1);
            if (x < n - 1) stack.Push(i + 1);
            if (y > 0) stack.Push(i - n);
            if (y < rows - 1) stack.Push(i + n);
        }
    }

    /// <summary>The colour picker tool: the emblem's colour under the pointer as the primary (or secondary) colour</summary>
    private static void PickColour(Vector2 p, bool right)
    {
        if (_previewSprite == null || _previewSprite.texture is not { } tex) {
            return;
        }

        int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
        if (x < 0 || y < 0 || x >= tex.width || y >= tex.height) {
            return;
        }

        var c = (Color32)tex.GetPixel(x, tex.height - 1 - y);
        if (c.a == 0) {
            return;
        }

        var rgba = ToRgba(c);
        if (right) {
            _secondary = rgba;
        } else {
            _primary = rgba;
        }

        ColourPicker.Remember(rgba);
        Fill();
    }

    /// <summary>A clear paint layer over the selection, selected (the Paint button, or a first stroke elsewhere)</summary>
    internal static void AddPaintLayer(bool fill = true)
    {
        // (a clear canvas of the whole emblem or drawing, one pixel of it per pixel of the canvas)
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers || EmblemPaint.Layer(CW, CH) is not { } layer) {
            return;
        }

        var list = _draft.Layers.ToList();
        var at = HasSelection ? _selected + 1 : list.Count;
        list.Insert(at, layer);
        Change(_draft with { Layers = list });
        Select(at);
        _picking = 0;
        _colourField = NoColour;
        if (!Paints(_tool) && _tool != ToolSelect) {
            _tool = ToolBrush;
        }

        if (fill) {
            Fill();
        }
    }

    // ---------------------------------------------------------------- the selection

    // the selection, in the canvas' pixels from its top left; the press that starts one, the pixels lifted to move
    private static RectInt? _sel;
    private static Vector2Int _selFrom;
    private static bool _selecting;
    private static bool _selMoving;
    private static Vector2Int _selGrab;
    private static RectInt _selStart;
    private static RectInt _liftRect;
    private static Color32[]? _lifted;
    private static Color32[]? _liftBase;

    // the pixels copied (Ctrl+C), and which clipboard was filled last: the pixels or a layer (the list's Copy)
    private static Color32[]? _pixClip;
    private static int _pixW, _pixH;
    private static bool _pixLast;

    private static Vector2Int CanvasPixel(Vector2 p) => new(Mathf.Clamp(Mathf.FloorToInt(p.x), 0, CW - 1), Mathf.Clamp(Mathf.FloorToInt(p.y), 0, CH - 1));

    private static void SelectDown(Vector2 p)
    {
        var at = CanvasPixel(p);
        // (a press inside the selection on a paint layer moves its pixels)
        if (_sel is { } sel && sel.Contains(new(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y))) && HasSelection && Current.IsPaint && Current.Lock == 0 &&
            EmblemPaint.Decode(Current.Thing) is { } decoded) {
            (_canvas, _pw, _ph) = decoded;
            _liftRect = LayerRect(Current, sel, _pw, _ph);
            _lifted = Cut(_canvas, _pw, _liftRect, false);
            _liftBase = (Color32[])_canvas.Clone();
            Clear(_liftBase, _pw, _liftRect);
            _selMoving = true;
            _selGrab = at;
            _selStart = sel;
            _dragKey = null;
            _reselect = true;
            return;
        }

        _selecting = true;
        _selFrom = at;
        _sel = null;
        ShowSelection();
    }

    private static void SelectDrag(Vector2 p)
    {
        var at = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
        if (_selMoving && _liftBase != null && _lifted != null && _canvas != null) {
            var d = at - _selGrab;
            var moved = Inside(new RectInt(_selStart.x + d.x, _selStart.y + d.y, _selStart.width, _selStart.height));
            _sel = moved.width > 0 && moved.height > 0 ? moved : _sel;
            // (in the layer's pixels: the same move scaled as the layer is)
            var o = LayerPixel(Current, _selGrab + (Vector2)d, _pw, _ph) - LayerPixel(Current, _selGrab, _pw, _ph);
            System.Array.Copy(_liftBase, _canvas, _canvas.Length);
            Stamp(_canvas, _pw, _ph, _lifted, _liftRect.width, _liftRect.height, _liftRect.x + o.x, _liftRect.y + o.y);
            Commit();
            ShowSelection();
            return;
        }

        if (!_selecting) {
            return;
        }

        var to = CanvasPixel(p);
        int x0 = Mathf.Min(_selFrom.x, to.x), y0 = Mathf.Min(_selFrom.y, to.y);
        _sel = new RectInt(x0, y0, Mathf.Abs(to.x - _selFrom.x) + 1, Mathf.Abs(to.y - _selFrom.y) + 1);
        ShowSelection();
    }

    private static void SelectUp()
    {
        if (_selMoving) {
            _selMoving = false;
            _canvas = null;
            _lifted = _liftBase = null;
            _dragKey = null;
            BakeNow();
            return;
        }

        if (_selecting) {
            _selecting = false;
            // (a click without a drag drops the selection)
            if (_sel is { width: <= 1, height: <= 1 }) {
                _sel = null;
            }

            ShowSelection();
            _reselect = true;
        }
    }

    /// <summary>The pixels of a rectangle of the canvas (rows top-down), cleared from it when <paramref name="clear" /></summary>
    private static Color32[] Cut(Color32[] px, int w, RectInt r, bool clear)
    {
        var part = new Color32[r.width * r.height];
        for (var y = 0; y < r.height; y++) {
            for (var x = 0; x < r.width; x++) {
                var i = (r.y + y) * w + r.x + x;
                part[y * r.width + x] = px[i];
                if (clear) {
                    px[i] = default;
                }
            }
        }

        return part;
    }

    private static void Clear(Color32[] px, int w, RectInt r) => Cut(px, w, r, true);

    /// <summary>Pixels laid over a canvas at that place (clear ones leave what is under them), clipped to it</summary>
    private static void Stamp(Color32[] px, int w, int h, Color32[] part, int pw, int ph, int ox, int oy)
    {
        for (var y = 0; y < ph; y++) {
            for (var x = 0; x < pw; x++) {
                int tx = ox + x, ty = oy + y;
                var c = part[y * pw + x];
                if (tx < 0 || ty < 0 || tx >= w || ty >= h || c.a == 0) {
                    continue;
                }

                px[ty * w + tx] = c.a == 255 ? c : Over(px[ty * w + tx], c);
            }
        }
    }

    private static Color32 Over(Color32 under, Color32 c)
    {
        var src = c.a / 255f;
        var dst = under.a / 255f * (1f - src);
        var a = src + dst;
        if (a <= 0.001f) {
            return default;
        }

        byte Ch(byte i, byte u) => (byte)Mathf.Clamp(Mathf.RoundToInt((i * src + u * dst) / a), 0, 255);
        return new(Ch(c.r, under.r), Ch(c.g, under.g), Ch(c.b, under.b), (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
    }

    /// <summary>A rectangle of the canvas' pixels cut to the canvas</summary>
    private static RectInt Inside(RectInt r)
    {
        int x0 = Mathf.Clamp(r.xMin, 0, CW), y0 = Mathf.Clamp(r.yMin, 0, CH), x1 = Mathf.Clamp(r.xMax, 0, CW), y1 = Mathf.Clamp(r.yMax, 0, CH);
        return new(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
    }

    /// <summary>The whole canvas selected (Ctrl+A)</summary>
    internal static void SelectAll()
    {
        _sel = new RectInt(0, 0, CW, CH);
        Fill();
    }

    internal static void Deselect()
    {
        _sel = null;
        Fill();
    }

    /// <summary>
    ///     The selection's pixels copied (of the selected paint layer; of the picture as seen when another kind of
    ///     layer is selected); without a selection, the selected layer itself (the list's Copy)
    /// </summary>
    internal static void CopySelection(bool cut = false)
    {
        if (_sel is not { } sel) {
            if (HasSelection) {
                _clipboard = Current;
                _pixLast = false;
                _note = "emp_ui_editor_copied_layer".lang();
                if (cut && Current.Lock == 0) {
                    RemoveLayer(_selected);
                    return;
                }

                Fill();
            }

            return;
        }

        if (HasSelection && Current.IsPaint && EmblemPaint.Decode(Current.Thing) is { } d) {
            var r = LayerRect(Current, sel, d.W, d.H);
            _pixClip = Cut(d.Px, d.W, r, false);
            (_pixW, _pixH) = (r.width, r.height);
            if (cut && Current.Lock == 0) {
                Clear(d.Px, d.W, r);
                if ((EmblemPaint.Encode(d.Px, d.W, d.H) ?? EmblemPaint.EncodeReduced(d.Px, d.W, d.H)) is { } code) {
                    Edit(l => l with { Thing = code });
                }
            }
        } else {
            // (what is seen: the whole picture baked, rows turned top-down)
            var seen = TopDown(TeamEmblemBaker.Bake(_draft), CW, CH);
            var inside = Inside(sel);
            _pixClip = Cut(seen, CW, inside, false);
            (_pixW, _pixH) = (inside.width, inside.height);
        }

        _pixLast = true;
        _note = "emp_ui_editor_copied".lang();
        Fill();
    }

    /// <summary>The copied pixels as a new paint layer, where the selection is (or the top left), selected to move them</summary>
    internal static void PastePixels()
    {
        if (!_pixLast || _pixClip == null) {
            Paste();
            return;
        }

        if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
            _note = "emp_ui_editor_layers_full".lang();
            Fill();
            return;
        }

        var at = _sel is { } sel ? sel.position : Vector2Int.zero;
        var px = EmblemPaint.Blank(CW, CH);
        Stamp(px, CW, CH, _pixClip, _pixW, _pixH, at.x, at.y);
        if (PaintLayerOf(px) is not { } layer) {
            return;
        }

        var list = _draft.Layers.ToList();
        var index = HasSelection ? _selected + 1 : list.Count;
        list.Insert(index, layer with { Name = "emp_ui_editor_pasted".lang() });
        Change(_draft with { Layers = list });
        Select(index);
        _sel = new RectInt(at.x, at.y, Mathf.Min(_pixW, CW - at.x), Mathf.Min(_pixH, CH - at.y));
        _tool = ToolSelect;
        Fill();
    }

    /// <summary>The selection's pixels cleared on the selected paint layer (Delete)</summary>
    internal static void DeleteSelection()
    {
        if (_sel is not { } sel || !HasSelection || !Current.IsPaint || Current.Lock != 0 || EmblemPaint.Decode(Current.Thing) is not { } d) {
            return;
        }

        Clear(d.Px, d.W, LayerRect(Current, sel, d.W, d.H));
        if ((EmblemPaint.Encode(d.Px, d.W, d.H) ?? EmblemPaint.EncodeReduced(d.Px, d.W, d.H)) is { } code) {
            Edit(l => l with { Thing = code });
        }

        Fill();
    }

    /// <summary>A whole canvas of pixels (rows top-down) as a paint layer covering it, null when it cannot be one</summary>
    private static EmblemLayer? PaintLayerOf(Color32[] px)
    {
        if (EmblemPaint.Layer(CW, CH) is not { } layer || (EmblemPaint.Encode(px, CW, CH) ?? EmblemPaint.EncodeReduced(px, CW, CH)) is not { } code) {
            return null;
        }

        return layer with { Thing = code };
    }

    /// <summary>A texture's rows (bottom-up) turned top-down</summary>
    private static Color32[] TopDown(Color32[] px, int w, int h)
    {
        var flipped = new Color32[w * h];
        for (var y = 0; y < h; y++) {
            System.Array.Copy(px, y * w, flipped, (h - 1 - y) * w, w);
        }

        return flipped;
    }
}
