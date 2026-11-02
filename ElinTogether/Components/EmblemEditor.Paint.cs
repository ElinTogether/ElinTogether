using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The editor's paint tools (lead dev 2026-11-02: "think of paint.net"): a tool bar (move, pencil, brush, eraser,
///     fill, line, rectangle, ellipse, colour picker, text), a primary and a secondary colour (left and right button)
///     with a palette under the canvas, the brush's size, filled shapes, the canvas' zoom and its pixel grid. The
///     paint tools draw into the selected paint layer (<see cref="EmblemPaint" />); on another layer, a first stroke
///     adds a paint layer over it. A stroke is one undo step
/// </summary>
internal static partial class EmblemEditor
{
    internal const int ToolMove = 0, ToolPencil = 1, ToolBrush = 2, ToolEraser = 3, ToolFill = 4, ToolLine = 5, ToolRect = 6,
        ToolEllipse = 7, ToolPicker = 8, ToolText = 9;

    private const int ToolCount = 10;

    private static readonly string[] ToolTips = [
        "emp_ui_emblem_tool_move", "emp_ui_emblem_tool_pencil", "emp_ui_emblem_tool_brush", "emp_ui_emblem_tool_eraser",
        "emp_ui_emblem_tool_fill", "emp_ui_emblem_tool_line", "emp_ui_emblem_tool_rect", "emp_ui_emblem_tool_ellipse",
        "emp_ui_emblem_tool_picker", "emp_ui_emblem_tool_text",
    ];

    // the colour fields of the pair (beside the layer's own, NoColour..GradColour)
    private const int Primary = 10, Secondary = 11;

    private static int _tool = ToolMove;
    private static uint _primary = 0x1A140FFFu;
    private static uint _secondary = 0xFFFFFFFFu;
    private static int _brush = 1;
    private static bool _filled;
    private static int _zoom;
    private static bool _grid = true;

    // the stroke being drawn: its colour, the canvas when it began (lines and shapes are drawn again over it), where
    // it began and where the pointer last was, in the layer's pixels
    private static bool _painting;
    private static Color32 _ink;
    private static Color32[]? _canvas;
    private static Color32[]? _strokeBase;
    private static Vector2Int _from;
    private static Vector2Int _last;

    // the paint layer's size in pixels while a stroke is drawn
    private static int _pw = EmblemPaint.N, _ph = EmblemPaint.N;

    private static Texture2D? _gridTexture;
    private static int _gridZoom;

    /// <summary>
    ///     The zooms the canvas offers: the one that fits it in its frame (3x for an emblem; under 1 for a large
    ///     drawing), then the whole ones above it
    /// </summary>
    private static float[] Zooms
    {
        get {
            var fit = (FrameSize - 8f) / Mathf.Max(CW, CH);
            if (fit >= 1f) {
                fit = Mathf.Floor(fit);
            }

            return new[] { fit }.Concat(new[] { 1f, 2f, 3f, 4f, 6f, 8f }.Where(z => z > fit + 0.01f)).ToArray();
        }
    }

    private static float Zoom => Zooms[Mathf.Clamp(_zoom, 0, Zooms.Length - 1)];

    private static bool Paints(int tool) => tool is ToolPencil or ToolBrush or ToolEraser or ToolFill or ToolLine or ToolRect or ToolEllipse;

    // ---------------------------------------------------------------- the bars

    /// <summary>The tool bar, and the primary and secondary colours at its end (paint.net's two squares)</summary>
    private static void ToolBar(YKLayout parent)
    {
        var bar = parent.Horizontal();
        bar.Layout.childForceExpandWidth = false;
        bar.Layout.childAlignment = TextAnchor.MiddleLeft;
        bar.Layout.spacing = 2f;
        bar.LayoutElement().minHeight = bar.LayoutElement().preferredHeight = 28f;
        for (var t = 0; t < ToolCount; t++) {
            var tool = t;
            var button = Icon(bar.IconButton(EditorIcons.Tool(t), "?", () => {
                _tool = tool;
                Fill();
            }, ToolTips[t], true, 0f, 28f));
            Highlight(button, tool == _tool);
        }

        var gap = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
        gap.transform.SetParent(bar.transform, false);
        gap.GetComponent<LayoutElement>().flexibleWidth = 1f;

        // the two colours: the primary over the secondary, a click opens the picker for it
        var pair = new GameObject("Colours", typeof(RectTransform), typeof(LayoutElement));
        pair.transform.SetParent(bar.transform, false);
        var element = pair.GetComponent<LayoutElement>();
        element.minWidth = element.preferredWidth = 34f;
        element.minHeight = element.preferredHeight = 28f;
        ((RectTransform)pair.transform).sizeDelta = new(34f, 28f);
        ColourSquare(pair.transform, _secondary, new(14f, -10f), Secondary);
        ColourSquare(pair.transform, _primary, new(0f, 0f), Primary);
        Icon(bar.IconButton(EditorIcons.Swap, "<>", () => {
            (_primary, _secondary) = (_secondary, _primary);
            Fill();
        }, "emp_ui_emblem_swap", true, 0f, 22f));
    }

    /// <summary>A colour square of the pair: framed in ink, a click picks its colour</summary>
    private static void ColourSquare(Transform parent, uint colour, Vector2 at, int field)
    {
        var go = new GameObject("Colour", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new(0f, 1f);
        rt.anchoredPosition = at;
        rt.sizeDelta = new(18f, 18f);
        go.GetComponent<Image>().color = new(0.28f, 0.22f, 0.13f, 0.9f);
        var inner = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        inner.transform.SetParent(go.transform, false);
        var irt = (RectTransform)inner.transform;
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new(2f, 2f);
        irt.offsetMax = new(-2f, -2f);
        var image = inner.GetComponent<Image>();
        image.color = ColourPicker.ToColor(colour);
        image.raycastTarget = false;
        go.GetComponent<Button>().onClick.AddListener(() => {
            SE.ClickGeneral();
            _colourField = field;
            Fill();
        });
    }

    /// <summary>The selected tool's button shaded as the journal's picked row</summary>
    private static void Highlight(UIButton button, bool on)
    {
        var shade = new GameObject("Picked", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        shade.transform.SetParent(button.transform, false);
        shade.transform.SetAsFirstSibling();
        shade.GetComponent<LayoutElement>().ignoreLayout = true;
        var rt = (RectTransform)shade.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var image = shade.GetComponent<Image>();
        image.color = on ? new(0.28f, 0.22f, 0.13f, 0.22f) : new(0f, 0f, 0f, 0f);
        image.raycastTarget = false;
    }

    /// <summary>
    ///     Under the canvas: the palette (a left click sets the primary colour, a right click the secondary), the
    ///     brush's size, filled shapes, the zoom and the grid
    /// </summary>
    private static void PaintOptions(YKLayout parent)
    {
        var palette = parent.Horizontal();
        palette.Layout.childForceExpandWidth = false;
        palette.Layout.spacing = 1f;
        palette.LayoutElement().minHeight = palette.LayoutElement().preferredHeight = 14f;
        foreach (var rgb in TeamBanner.Palette) {
            var rgba = ((uint)rgb << 8) | 0xFFu;
            var go = new GameObject("Paint", typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(EventTrigger));
            go.transform.SetParent(palette.transform, false);
            ((RectTransform)go.transform).sizeDelta = new(14f, 14f);
            var element = go.GetComponent<LayoutElement>();
            (element.minWidth, element.preferredWidth, element.minHeight, element.preferredHeight) = (14f, 14f, 14f, 14f);
            go.GetComponent<Image>().color = ColourPicker.ToColor(rgba);
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(data => {
                SE.ClickGeneral();
                if (data is PointerEventData { button: PointerEventData.InputButton.Right }) {
                    _secondary = rgba;
                } else {
                    _primary = rgba;
                }

                Fill();
            });
            go.GetComponent<EventTrigger>().triggers.Add(entry);
        }

        var row = parent.Row();
        row.Layout.spacing = 4f;
        // the brush's size (pencil 1 px; brush, eraser and lines), filled shapes
        var sized = _tool is ToolBrush or ToolEraser or ToolLine or ToolRect or ToolEllipse;
        var label = row.TextSmall("emp_ui_emblem_brush".lang(), FontColor.Gray);
        label.LayoutElement().minWidth = 0f;
        row.SignButton("-", () => {
            _brush = Mathf.Max(1, _brush - 1);
            Fill();
        }, 24f).interactable = sized && _brush > 1;
        var size = row.Text(_brush + " px");
        size.LayoutElement().minWidth = size.LayoutElement().preferredWidth = 38f;
        size.alignment = TextAnchor.MiddleCenter;
        row.SignButton("+", () => {
            _brush = Mathf.Min(6, _brush + 1);
            Fill();
        }, 24f).interactable = sized && _brush < 6;
        var filled = row.Toggle("emp_ui_emblem_filled".lang(), _filled, on => _filled = on);
        filled.AsRow();
        filled.interactable = _tool is ToolRect or ToolEllipse;

        var gap = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
        gap.transform.SetParent(row.transform, false);
        gap.GetComponent<LayoutElement>().flexibleWidth = 1f;

        // the zoom: 3x shows the whole emblem; more scrolls it (the wheel, or a drag with the middle button)
        row.SignButton("-", () => {
            _zoom = Mathf.Max(0, _zoom - 1);
            Fill();
        }, 24f).interactable = _zoom > 0;
        var zoom = row.Text(Zoom >= 1f ? Mathf.RoundToInt(Zoom) + "x" : Mathf.RoundToInt(Zoom * 100f) + "%");
        zoom.LayoutElement().minWidth = zoom.LayoutElement().preferredWidth = 28f;
        zoom.alignment = TextAnchor.MiddleCenter;
        row.SignButton("+", () => {
            _zoom = Mathf.Min(Zooms.Length - 1, _zoom + 1);
            Fill();
        }, 24f).interactable = _zoom < Zooms.Length - 1;
        var grid = Icon(row.IconButton(EditorIcons.Grid, "#", () => {
            _grid = !_grid;
            Fill();
        }, "emp_ui_emblem_grid", true, 0f, 24f));
        Highlight(grid, _grid);
        // the drag's snap to the 4 px grid and the centre
        var snap = Icon(row.IconButton(EditorIcons.Magnet, "S", () => {
            _snap = !_snap;
            Fill();
        }, "emp_ui_emblem_snap", true, 0f, 24f));
        Highlight(snap, _snap);
    }

    // ---------------------------------------------------------------- the canvas

    /// <summary>
    ///     The canvas in its frame: the emblem at the zoom, scrolled inside the frame when larger, the pixel grid over
    ///     it from 4x. Returns the emblem's image (the pointer's target)
    /// </summary>
    private static Image Canvas(Transform frame, Sprite sprite)
    {
        var view = new GameObject("Canvas", typeof(RectTransform), typeof(ScrollRect));
        view.transform.SetParent(frame, false);
        var vrt = (RectTransform)view.transform;
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = new(2f, 2f);
        vrt.offsetMax = new(-2f, -2f);

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(view.transform, false);
        var crt = (RectTransform)content.transform;
        crt.anchorMin = crt.anchorMax = crt.pivot = new(0.5f, 0.5f);
        crt.sizeDelta = new(CW * Zoom, CH * Zoom);
        crt.anchoredPosition = Vector2.zero;

        var scroll = view.GetComponent<ScrollRect>();
        scroll.content = crt;
        scroll.viewport = vrt;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = 24f;
        scroll.horizontal = CW * Zoom > FrameSize - 4f;
        scroll.vertical = CH * Zoom > FrameSize - 4f;

        // (the canvas' own ground, a light checker as a paint program shows what is clear: its edges are seen even
        // when nothing is drawn near them, as on a new drawing)
        var ground = new GameObject("Ground", typeof(RectTransform), typeof(RawImage));
        ground.transform.SetParent(content.transform, false);
        var gr = (RectTransform)ground.transform;
        gr.anchorMin = Vector2.zero;
        gr.anchorMax = Vector2.one;
        gr.offsetMin = gr.offsetMax = Vector2.zero;
        var checker = ground.GetComponent<RawImage>();
        checker.texture = Checker();
        checker.uvRect = new(0f, 0f, CW / 8f, CH / 8f);
        checker.raycastTarget = false;

        var emblem = TeamEmblemBaker.Place(content.transform, sprite, Mathf.Max(CW, CH) * Zoom);
        emblem.preserveAspect = false;
        emblem.GetComponent<LayoutElement>().ignoreLayout = true;
        var rt = emblem.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        if (_grid && Zoom >= 4f && Mathf.Approximately(Zoom, Mathf.Round(Zoom))) {
            var grid = new GameObject("PixelGrid", typeof(RectTransform), typeof(RawImage));
            grid.transform.SetParent(content.transform, false);
            var grt = (RectTransform)grid.transform;
            grt.anchorMin = Vector2.zero;
            grt.anchorMax = Vector2.one;
            grt.offsetMin = grt.offsetMax = Vector2.zero;
            var raw = grid.GetComponent<RawImage>();
            raw.texture = GridTexture(Mathf.RoundToInt(Zoom));
            raw.uvRect = new(0f, 0f, CW, CH);
            raw.raycastTarget = false;
        }

        return emblem;
    }

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
    private static Vector2Int LayerPixel(EmblemLayer l, Vector2 p, int pw, int ph)
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
        return new(Mathf.FloorToInt(u / scale + pw / 2f), Mathf.FloorToInt(v / scale + ph / 2f));
    }

    private static Color32 ToColor32(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    /// <summary>A paint tool pressed on the canvas: the selected paint layer, or a new one over the selection</summary>
    private static void PaintDown(Vector2 p, bool right)
    {
        if (_tool == ToolPicker) {
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

        var added = false;
        if (!HasSelection || !Current.IsPaint || Current.Lock != 0 || Current.Hidden != 0) {
            if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
                return;
            }

            AddPaintLayer(false);
            added = true;
        }

        if (EmblemPaint.Decode(Current.Thing) is not { } decoded) {
            return;
        }

        (_canvas, _pw, _ph) = decoded;
        _strokeBase = (Color32[])_canvas.Clone();
        _ink = _tool == ToolEraser ? new Color32(0, 0, 0, 0) : ToColor32(right ? _secondary : _primary);
        _painting = true;
        _from = _last = LayerPixel(Current, p, _pw, _ph);
        // (a stroke that added its layer is one undo step with it)
        _dragKey = added ? "paint" : null;
        // (the list, the history and Save shown again when the pointer is released)
        _reselect = true;
        if (_tool == ToolFill) {
            Flood(_canvas, _from, _ink);
            _painting = false;
        } else {
            Stroke(_from);
        }

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
        _dragKey = null;
        BakeNow();
    }

    /// <summary>The stroke drawn to that point: a free line from the last point, or the line or shape from its start</summary>
    private static void Stroke(Vector2Int at)
    {
        if (_canvas == null || _strokeBase == null) {
            return;
        }

        var width = _tool == ToolPencil ? 1 : _brush;
        switch (_tool) {
            case ToolLine:
                System.Array.Copy(_strokeBase, _canvas, _canvas.Length);
                Line(_canvas, _from, at, width, _ink);
                break;
            case ToolRect:
            case ToolEllipse:
                System.Array.Copy(_strokeBase, _canvas, _canvas.Length);
                Shape(_canvas, _from, at, width, _ink, _tool == ToolEllipse, _filled);
                break;
            default:
                Line(_canvas, _last, at, width, _ink);
                break;
        }
    }

    /// <summary>The canvas into the selected layer (one undo step for the whole stroke); refused past 255 colours</summary>
    private static void Commit()
    {
        if (_canvas == null || EmblemPaint.Encode(_canvas, _pw, _ph) is not { } code) {
            return;
        }

        Edit(l => l with { Thing = code }, "paint");
    }

    private static void Dot(Color32[] px, int cx, int cy, int width, Color32 ink)
    {
        // (a square of 1 or 2, a round dab from 3)
        var r = (width - 1) / 2f;
        for (var y = Mathf.FloorToInt(cy - r); y <= Mathf.CeilToInt(cy + r); y++) {
            for (var x = Mathf.FloorToInt(cx - r); x <= Mathf.CeilToInt(cx + r); x++) {
                if (x < 0 || y < 0 || x >= _pw || y >= _ph) {
                    continue;
                }

                if (width >= 3 && (x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r + 0.5f) {
                    continue;
                }

                if (width == 2 && (x < cx || y < cy)) {
                    continue;
                }

                px[y * _pw + x] = ink;
            }
        }
    }

    /// <summary>A line of dabs from a to b (Bresenham)</summary>
    private static void Line(Color32[] px, Vector2Int a, Vector2Int b, int width, Color32 ink)
    {
        int x = a.x, y = a.y, dx = Mathf.Abs(b.x - a.x), dy = -Mathf.Abs(b.y - a.y);
        int sx = a.x < b.x ? 1 : -1, sy = a.y < b.y ? 1 : -1, err = dx + dy;
        for (var guard = 0; guard < 4 * Drawing.MaxSide * Drawing.MaxSide; guard++) {
            Dot(px, x, y, width, ink);
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
    private static void Shape(Color32[] px, Vector2Int a, Vector2Int b, int width, Color32 ink, bool ellipse, bool filled)
    {
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
                    px[y * _pw + x] = ink;
                }
            }
        }
    }

    /// <summary>The bucket: the area of the clicked pixel's colour (4-connected) in the ink</summary>
    private static void Flood(Color32[] px, Vector2Int at, Color32 ink)
    {
        int n = _pw, rows = _ph;
        if (at.x < 0 || at.y < 0 || at.x >= n || at.y >= rows) {
            return;
        }

        var target = px[at.y * n + at.x];
        bool Same(Color32 c) => c.a == 0 && target.a == 0 || c.r == target.r && c.g == target.g && c.b == target.b && c.a == target.a;
        if (Same(ink)) {
            return;
        }

        var stack = new Stack<int>();
        stack.Push(at.y * n + at.x);
        while (stack.Count > 0) {
            var i = stack.Pop();
            if (!Same(px[i])) {
                continue;
            }

            px[i] = ink;
            int x = i % n, y = i / n;
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

        var rgba = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
        if (right) {
            _secondary = rgba;
        } else {
            _primary = rgba;
        }

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
        if (!Paints(_tool)) {
            _tool = ToolBrush;
        }

        if (fill) {
            Fill();
        }
    }
}
