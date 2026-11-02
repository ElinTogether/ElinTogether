using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The editor's stickers (lead dev 2026-11-02: the game's pictures, items, monsters... "obvious and comfortable to
///     place"): a Stickers button near the top of the panel opens the picture grid under a Stickers heading, with the
///     favourites and the recent stickers over it (<see cref="StickerPrefs" />). A click lands the sticker as its own
///     layer in the middle of the part of the canvas in view and picks the Move tool, so the next drag moves it; a
///     cell dragged onto the canvas lands where it is dropped. With the Move tool, the selected picture, shape or text
///     has four square handles at its box's corners: dragging one scales it about its centre, a picture by whole steps
///     of its own pixels (its pixel art stays crisp), the others by whole percents; the panel turns and flips it and
///     makes a picture a favourite. Nothing of the emblem's text changes: a sticker is a picture layer as before
/// </summary>
internal static partial class EmblemEditor
{
    // a handle's side on the screen, in the canvas view's units
    private const float HandlePx = 8f;

    // the handle being dragged (0 top left, 1 top right, 2 bottom right, 3 bottom left; -1 none), the pointer's
    // distance from the layer's centre and the layer's size when the drag began
    private static int _handle = -1;
    private static float _handleFrom;
    private static int _handleSize;

    // the sticker grid's cell sides (lead dev 2026-11-02: bigger cells), the one picked (kept while the game runs)
    private static readonly float[] StickerCells = [38f, 50f, 64f, 80f];
    private static int _stickerCell;

    // ---------------------------------------------------------------- placing

    /// <summary>
    ///     A sticker as a new layer on top, at <paramref name="at" /> (a drop) or the middle of the canvas in view, at
    ///     a size that keeps its pixels whole; the Move tool picked so the next drag moves it. One undo step
    /// </summary>
    internal static void PickSticker(string thing, Vector2? at = null)
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
            _note = "emp_ui_editor_layers_full".lang();
            Fill();
            return;
        }

        var p = at ?? ViewCentre();
        var layer = Fitted(TeamEmblem.Layer(thing)) with { X = PosX(Mathf.Clamp(p.x, 0f, CW)), Y = PosY(Mathf.Clamp(p.y, 0f, CH)) };
        layer = layer with { Size = CrispSize(layer, layer.Size) };
        StickerPrefs.Used(thing);
        _tool = ToolMove;
        Push(layer);
    }

    /// <summary>The canvas' point in the middle of the view (from the zoom and the pan), its middle without a view</summary>
    private static Vector2 ViewCentre()
    {
        if (_view == null) {
            return new(CW / 2f, CH / 2f);
        }

        // (the canvas is centred in the view and moved by the pan, y up; its pixels count from the top)
        var z = Mathf.Max(0.01f, _zoomF);
        return new(Mathf.Clamp(CW / 2f - _pan.x / z, 0f, CW), Mathf.Clamp(CH / 2f + _pan.y / z, 0f, CH));
    }

    /// <summary>A grid cell released over the canvas' view: the sticker lands at that point. False elsewhere</summary>
    private static bool DropSticker(string thing, Vector2 screen)
    {
        if (_view == null || _view.Picture is not { } picture ||
            !RectTransformUtility.RectangleContainsScreenPoint((RectTransform)_view.transform, screen, _view.Cam)) {
            return false;
        }

        PickSticker(thing, ScreenToCanvas(picture, screen, _view.Cam));
        return true;
    }

    // ---------------------------------------------------------------- crisp sizes

    /// <summary>
    ///     The size nearest to <paramref name="raw" /> a layer may take from its handles: a pixel-art picture's at whole
    ///     steps of its own pixels (<see cref="Steps" />); else whole percents
    /// </summary>
    private static int CrispSize(EmblemLayer l, float raw)
    {
        if (Steps(l) is { Count: > 0 } steps) {
            return steps.OrderBy(s => Mathf.Abs(s - raw)).First();
        }

        return Mathf.Clamp(Mathf.RoundToInt(raw), TeamEmblem.MinSize, MaxLayerSize);
    }

    /// <summary>
    ///     The crisp sizes of a picture layer whose pixels read and whose longer side is at most the emblem's 64 (the
    ///     items, monsters, icons: pixel art). A larger picture (a portrait, a place: up to 256) is averaged when
    ///     shrunk anyway, and its few whole steps (100 % and over) would keep it from being made small: null
    /// </summary>
    private static List<int>? Steps(EmblemLayer l)
    {
        if (!l.IsSprite || l.IsPaint || TeamEmblemBaker.SourceSize(l.Thing) is not { } src || Mathf.Max(src.W, src.H) > TeamEmblem.Grid) {
            return null;
        }

        return CrispSteps(Mathf.Max(src.W, src.H, 1), TeamEmblem.MinSize, MaxLayerSize);
    }

    /// <summary>
    ///     The sizes (% of 64 pixels, the longer side) at which a picture whose longer side is <paramref name="native" />
    ///     pixels draws each of its pixels as k canvas pixels (k = 1, 2, 3...), or k of its pixels as one (k = 2..4).
    ///     Rounded so the scale is never under the whole step: the nearest pixel then never drops one of its pixels
    /// </summary>
    internal static List<int> CrispSteps(int native, int min, int max)
    {
        var steps = new List<int>();
        for (var k = 1; k <= 64; k++) {
            var s = Mathf.CeilToInt(100f * native * k / TeamEmblem.Grid - 0.001f);
            if (s > max) {
                break;
            }

            if (s >= min) {
                steps.Add(s);
            }
        }

        // (shrunk: the bake averages k by k of its pixels while floor(1/scale) is k: the scale kept just under 1/k, so
        // the float division never lands on k - 1)
        for (var k = 2; k <= 4; k++) {
            var s = Mathf.FloorToInt(100f * native / (TeamEmblem.Grid * k) - 0.001f);
            if (s >= min && s <= max) {
                steps.Add(s);
            }
        }

        return steps.Distinct().OrderBy(s => s).ToList();
    }

    // ---------------------------------------------------------------- the resize handles

    // the selected layer has handles: a picture, a shape or a text, free, shown, with the Move tool
    private static bool Resizable => _tool == ToolMove && HasSelection && !Current.IsPaint && Current.Lock == 0 && Current.Hidden == 0;

    /// <summary>Half the layer's box on the canvas (its picture's own shape when it reads, else its square), turned</summary>
    private static Vector2 HalfBox(EmblemLayer l)
    {
        var side = TeamEmblem.Grid * l.Size / 100f;
        float w = side, h = side;
        if (l.IsSprite && TeamEmblemBaker.SourceSize(l.Thing) is { } src) {
            var scale = side / Mathf.Max(src.W, src.H, 1);
            (w, h) = (src.W * scale, src.H * scale);
        }

        var a = l.Rotation * Mathf.Deg2Rad;
        float c = Mathf.Abs(Mathf.Cos(a)), s = Mathf.Abs(Mathf.Sin(a));
        return new((w * c + h * s) / 2f, (w * s + h * c) / 2f);
    }

    /// <summary>A corner of the layer's box in the canvas' pixels: 0 top left, 1 top right, 2 bottom right, 3 bottom left</summary>
    private static Vector2 Corner(EmblemLayer l, int corner)
    {
        var half = HalfBox(l);
        return new(l.X + (corner is 1 or 2 ? half.x : -half.x), l.Y + (corner >= 2 ? half.y : -half.y));
    }

    /// <summary>The handle under that point of the canvas, or -1</summary>
    private static int HandleAt(Vector2 p)
    {
        if (!Resizable || _view == null) {
            return -1;
        }

        // (a few screen pixels around the handle, at any zoom)
        var reach = Mathf.Max(0.75f, (HandlePx / 2f + 3f) / Mathf.Max(0.01f, _zoomF));
        for (var i = 0; i < 4; i++) {
            var c = Corner(Current, i);
            if (Mathf.Abs(p.x - c.x) <= reach && Mathf.Abs(p.y - c.y) <= reach) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A press of the Move tool: on a handle, the drag resizes the selected layer</summary>
    private static bool HandleDown(Vector2 p)
    {
        var corner = HandleAt(p);
        if (corner < 0) {
            return false;
        }

        _handle = corner;
        _handleSize = Current.Size;
        _handleFrom = Mathf.Max(0.5f, Vector2.Distance(p, new(Current.X, Current.Y)));
        _dragKey = null;
        return true;
    }

    /// <summary>The handle dragged: the layer scaled about its centre as far as the pointer is from it (one undo step)</summary>
    private static void HandleDrag(Vector2 p)
    {
        if (!HasSelection) {
            return;
        }

        var raw = _handleSize * Vector2.Distance(p, new(Current.X, Current.Y)) / _handleFrom;
        var size = CrispSize(Current, raw);
        if (size != Current.Size) {
            Edit(l => l with { Size = size }, "resize");
        }
    }

    /// <summary>The four handles made in the canvas' content, over the picture (placed by <see cref="ShowHandles" />)</summary>
    private static RectTransform[] Handles(RectTransform content)
    {
        var handles = new RectTransform[4];
        for (var i = 0; i < 4; i++) {
            var box = Child(content, "Handle" + i, typeof(Image));
            var image = box.GetComponent<Image>();
            image.color = new(0.1f, 0.08f, 0.06f, 0.95f);
            image.raycastTarget = false;
            var inner = Child(box, "Fill", typeof(Image));
            inner.anchorMin = Vector2.zero;
            inner.anchorMax = Vector2.one;
            inner.offsetMin = new(1f, 1f);
            inner.offsetMax = new(-1f, -1f);
            var fill = inner.GetComponent<Image>();
            fill.color = new(1f, 1f, 1f, 0.95f);
            fill.raycastTarget = false;
            box.gameObject.SetActive(false);
            handles[i] = box;
        }

        return handles;
    }

    /// <summary>The handles at the selected layer's corners, the same size on the screen at any zoom; hidden without one</summary>
    private static void ShowHandles()
    {
        if (View?.Handles is not { } handles) {
            return;
        }

        var on = Resizable;
        var side = HandlePx / Mathf.Max(0.01f, _zoomF);
        for (var i = 0; i < handles.Length; i++) {
            if (handles[i] == null) {
                continue;
            }

            handles[i].gameObject.SetActive(on);
            if (on) {
                var c = Corner(Current, i);
                Place(handles[i], c.x - side / 2f, c.y - side / 2f, side, side);
            }
        }
    }

    // ---------------------------------------------------------------- the panel

    /// <summary>The Stickers button, near the top of the panel: the picture grid with the recent stickers and the favourites</summary>
    private static void StickerSection(YKLayout side)
    {
        var row = side.ButtonRow();
        var button = Even(row.RowButton("emp_ui_editor_stickers".lang(), () => OpenGrid(PickLayer)));
        button.name = "EditorStickers";
        button.interactable = _draft.Layers.Count < TeamEmblem.MaxLayers;
        Tip(button, "emp_ui_editor_stickers_tip".lang());
    }

    /// <summary>Over the grid: how to place one, the favourites, the recent stickers</summary>
    private static void StickerRows(YKLayout side)
    {
        side.TextSmall("emp_ui_editor_sticker_help".lang(), FontColor.Gray);
        // the grid's pictures smaller or larger
        var zoom = side.Row();
        zoom.Layout.spacing = 6f;
        var title = zoom.TextSmall("emp_ui_editor_sticker_cells".lang(), FontColor.Gray);
        title.LayoutElement().flexibleWidth = 1f;
        title.LayoutElement().preferredWidth = 0f;
        zoom.SignButton("-", () => CellStep(-1), 28f).name = "EditorStickerSmaller";
        zoom.SignButton("+", () => CellStep(1), 28f).name = "EditorStickerBigger";
        StickerRow(side, "emp_ui_editor_sticker_favourites", StickerPrefs.Favourites, "EditorStickerFavourites");
        StickerRow(side, "emp_ui_editor_sticker_recent", StickerPrefs.Recent, "EditorStickerRecent");
    }

    /// <summary>A row of stickers (their title over them): a click places one as a cell of the grid does</summary>
    private static void StickerRow(YKLayout side, string titleId, IReadOnlyList<string> things, string name)
    {
        var shown = things.Select(t => (Thing: t, Sprite: TeamBanner.Cropped(TeamBanner.SpriteOf(t)))).Where(x => x.Sprite != null).ToList();
        if (shown.Count == 0) {
            return;
        }

        side.TextSmall(titleId.lang(), FontColor.Gray);
        const float cell = 30f;
        const int columns = 11;
        var rows = (shown.Count + columns - 1) / columns;
        var height = rows * (cell + 2f);
        var box = Child(side.transform, name, typeof(LayoutElement), typeof(GridLayoutGroup));
        var element = box.GetComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = height;
        element.flexibleWidth = 1f;
        box.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        var grid = box.GetComponent<GridLayoutGroup>();
        grid.cellSize = new(cell, cell);
        grid.spacing = new(2f, 2f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;
        foreach (var (thing, sprite) in shown) {
            var go = Child(box, "Sticker:" + thing, typeof(Image), typeof(Button));
            go.GetComponent<Image>().color = new(Dark.r, Dark.g, Dark.b, 0.35f);
            var pic = Child(go, "Picture", typeof(Image));
            pic.anchorMin = pic.anchorMax = pic.pivot = new(0.5f, 0.5f);
            var inner = cell - 6f;
            var scale = inner / Mathf.Max(sprite!.rect.width, sprite.rect.height, 1f);
            pic.sizeDelta = new(sprite.rect.width * scale, sprite.rect.height * scale);
            var image = pic.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            var picked = thing;
            go.GetComponent<Button>().onClick.AddListener(() => {
                SE.ClickGeneral();
                Picked(PickLayer, picked);
            });
        }
    }

    /// <summary>
    ///     The Move tool's options for the selected layer: turn left and right by a quarter, flip across and down; its
    ///     size and how the handles change it; a picture made a favourite sticker, or no longer one
    /// </summary>
    private static void MoveOptions(YKLayout side)
    {
        if (!HasSelection || Current.Lock != 0) {
            return;
        }

        var turn = side.ButtonRow();
        Even(turn.RowButton("emp_ui_editor_rotate_left".lang(), () => EditNow(l => l with { Rotation = ((l.Rotation - 90) % 360 + 360) % 360 }))).name = "EditorRotateLeft";
        Even(turn.RowButton("emp_ui_editor_rotate_right".lang(), () => EditNow(l => l with { Rotation = (l.Rotation + 90) % 360 }))).name = "EditorRotateRight";
        var flips = side.ButtonRow();
        Even(flips.RowButton("emp_ui_emblem_flip_h".lang(), () => EditNow(l => l with { Flip = l.Flip ^ 1 }))).name = "EditorFlipH";
        Even(flips.RowButton("emp_ui_emblem_flip_v".lang(), () => EditNow(l => l with { Flip = l.Flip ^ 2 }))).name = "EditorFlipV";
        if (!Current.IsPaint) {
            side.TextSmall("emp_ui_editor_sticker_size".Loc(Current.Size), FontColor.Gray);
        }

        // a pixel-art picture at 1, 2, 3 or 4 canvas pixels for each of its own (lead dev 2026-11-02: size presets)
        if (Current.IsSprite && !Current.IsPaint && TeamEmblemBaker.SourceSize(Current.Thing) is { } src && Mathf.Max(src.W, src.H) <= TeamEmblem.Grid) {
            var native = Mathf.Max(src.W, src.H, 1);
            var row = side.Row();
            row.Layout.spacing = 4f;
            var title = row.TextSmall("emp_ui_editor_pixel_size".lang(), FontColor.Gray);
            title.LayoutElement().flexibleWidth = 1f;
            title.LayoutElement().preferredWidth = 0f;
            for (var k = 1; k <= 4; k++) {
                var size = PixelSize(native, k);
                var button = Small(row.Button(k + "x", () => EditNow(l => l with { Size = size })));
                button.name = "EditorPixel" + k;
                button.interactable = size <= MaxLayerSize;
                Highlight(button, Current.Size == size);
            }
        }

        if (Current.IsSprite && !Current.IsPaint) {
            var thing = Current.Thing;
            var label = StickerPrefs.IsFavourite(thing) ? "emp_ui_editor_sticker_unfavourite" : "emp_ui_editor_sticker_favourite";
            Even(side.ButtonRow().RowButton(label.lang(), () => {
                StickerPrefs.ToggleFavourite(thing);
                Fill();
            })).name = "EditorFavourite";
        }
    }

    /// <summary>The size (% of 64, the longer side) at which a picture of that many pixels draws each of them as k canvas pixels</summary>
    internal static int PixelSize(int native, int k) => Mathf.Clamp(Mathf.CeilToInt(100f * native * k / TeamEmblem.Grid - 0.001f), TeamEmblem.MinSize, 100000);

    private static void CellStep(int by)
    {
        var next = Mathf.Clamp(_stickerCell + by, 0, StickerCells.Length - 1);
        if (next != _stickerCell) {
            _stickerCell = next;
            Fill();
        }
    }

    private static void EditNow(System.Func<EmblemLayer, EmblemLayer> change)
    {
        Edit(change);
        Fill();
    }

#if DEBUG
    // ---------------------------------------------------------------- the bench (Debug eval)

    /// <summary>What a click on a cell of the sticker grid does (its onPick): the sticker placed. "ok", or "full"</summary>
    internal static string BenchPickSticker(string thing)
    {
        var before = _draft.Layers.Count;
        Picked(PickLayer, thing);
        return _draft.Layers.Count > before ? "ok" : "full";
    }

    /// <summary>A grid cell's drag released over that pixel of the canvas (the screen point the input module gives)</summary>
    internal static string BenchDropSticker(string thing, float x, float y) => DropSticker(thing, CanvasToScreen(x, y)) ? "ok" : "outside";

    /// <summary>
    ///     The stickers' state for the bench: handles shown|the four corners (x,y;...)|the view's centre (x,y)|the
    ///     selected picture's crisp sizes (a,b,...; "-" when its handles go by whole percents)|recent (id;id...)|favourites|the selected layer's thing
    /// </summary>
    /// <summary>The sticker grid for the bench: its cell side|the entries it lists|its scroll (the content's y); "-" without one</summary>
    internal static string BenchStickerGrid
    {
        get {
            var grid = _host != null ? _host.GetComponentInChildren<SpriteGridView>(false) : null;
            if (grid == null || grid.GetComponent<ScrollRect>() is not { } scroll || scroll.content == null) {
                return "-";
            }

            return string.Join("|", StickerCells[_stickerCell].ToString(CultureInfo.InvariantCulture), grid.Count,
                scroll.content.anchoredPosition.y.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    internal static string BenchSticker
    {
        get {
            string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
            var corners = HasSelection ? string.Join(";", Enumerable.Range(0, 4).Select(i => Corner(Current, i)).Select(c => F(c.x) + "," + F(c.y))) : "-";
            var centre = ViewCentre();
            var steps = HasSelection && Steps(Current) is { Count: > 0 } list ? string.Join(",", list) : "-";
            var shown = View?.Handles is { } hs && hs.Any(h => h != null && h.gameObject.activeSelf);
            return string.Join("|", shown, corners, F(centre.x) + "," + F(centre.y), steps, string.Join(";", StickerPrefs.Recent),
                string.Join(";", StickerPrefs.Favourites), HasSelection ? Current.Thing : "-");
        }
    }
#endif
}
