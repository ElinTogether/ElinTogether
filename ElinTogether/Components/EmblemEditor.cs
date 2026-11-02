using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The leader's emblem creator on the Team tab's Emblem page (PLAN_team_emblem, lead dev 2026-11-01 "develop it
///     much further"). Left page: a 3x preview where a click picks the layer under the pointer and a drag moves it (the
///     selection with it, snapped to a 4 px grid and the centre when Snap is on), chips at 48, 24 and 16 px beside it
///     to see how it reads small; add a shape, a picture of the game (<see cref="SpriteGrid" />, many sources) or a
///     text; undo and redo (40 steps), save; random, templates, the emblem as a code to copy or paste; cut to the
///     bottom layer, snap. Right page: the layers (shift-click to select several; hide, lock, up, down, remove), copy,
///     paste, all, the align and spread buttons, then the selected layer's tools on three pills: Place (move pad, turn,
///     flips, size, angle, opacity, duplicate, mirror), Look (text, font, alignment, spacing, curve; colours by the
///     <see cref="ColourPicker" />, pattern, texture, points, height, thickness, outline) and Effects (name, live
///     mirror, blend, clip to the layer below, shadow, gradient). The previews bake into the editor's own textures,
///     at most every 50 ms while dragging, and once more when it stops. The draft stays in this game until Save asks
///     the host
/// </summary>
internal static partial class EmblemEditor
{
    private const int UndoMax = 40;
    private const int Step = 2;
    private const int SnapGrid = 4;
    private const float PreviewSize = TeamEmblem.Grid * 3f;
    private const float FrameSize = PreviewSize + 12f;

    // the layer list: rows shown before it scrolls, a row's height
    private const int ListRows = 4;
    private const float LayerRowHeight = 30f;

    // what a row of the page's buttons shares (a page of the book is about 360 wide at the window's normal size)
    private const float PageWidth = 356f;
    private const float BakeEvery = 0.05f;
    private const int ThumbCapacity = 48;

    // the colour being picked
    private const int NoColour = 0, Fill1 = 1, Fill2 = 2, OutlineColour = 3, ShadowColour = 4, GradColour = 5;

    // what a click in the picture grid does: add a layer, or give the selected shape its texture
    private const int PickLayer = 1, PickTexture = 2;

    private static int _team = -1;
    private static Emblem _draft = new([]);
    private static int _selected;
    private static readonly HashSet<int> _multi = [];
    private static int _picking;
    private static int _colourField;
    private static int _toolPage;
    private static bool _snap = true;
    private static EmblemLayer? _clipboard;
    private static string? _note;
    private static readonly List<Emblem> _undo = [];
    private static readonly List<Emblem> _redo = [];

    // a drag (a slider's, the preview's, the colour picker's, typing) being one undo step, not one per value
    private static string? _dragKey;

    // the preview's pointer: the layer moved and where it was taken from it, and a selection to redraw on release
    private static bool _moving;
    private static Vector2 _grab;
    private static bool _reselect;

    private static YKLayout? _left;
    private static YKLayout? _right;
    private static bool _leads;

    // the editor's own bakes (review 2026-11-01: never the shared cache, whose LRU destroys sprites still shown)
    private static Sprite? _previewSprite;
    private static bool _dirty;
    private static float _lastBake = -1f;
    private static readonly Dictionary<string, LinkedListNode<(string Key, Sprite Sprite)>> _thumbs = [];
    private static readonly LinkedList<(string Key, Sprite Sprite)> _thumbOrder = new();

    private static readonly Color Dark = new(0.2f, 0.17f, 0.13f, 1f);

    /// <summary>
    ///     The Emblem page of the Team tab: the big preview and the emblem's buttons on the left page; the layers and
    ///     the selected layer's tools (or the picture grid, or the colour picker, while open) on the right page.
    ///     Anyone without the emblem right sees the emblem and its layers only
    /// </summary>
    internal static void Build(YKLayout left, YKLayout right, int teamId, bool leads)
    {
        _team = teamId;
        _doc = null;
        _docId = null;
        Enter("team:" + teamId, () => Saved(teamId));
        _left = left;
        _right = right;
        _leads = leads;
        if (!leads) {
            _draft = Saved(teamId);
        }

        Fill();
    }

    /// <summary>
    ///     A drawing of My drawings open in the same editor (<see cref="DrawingsPage" />): its own canvas size, saved
    ///     to the player's list, closed back to the list. Every editor tool works the same
    /// </summary>
    internal static void BuildDrawing(YKLayout left, YKLayout right, string id, Drawing doc, System.Action close, System.Action<string> forked)
    {
        if (_docId != id) {
            _closing = false;
        }

        _doc = doc;
        _docId = id;
        _closeDoc = close;
        _forked = forked;
        Enter("drawing:" + id, () => doc.Art);
        _left = left;
        _right = right;
        _leads = true;
        Fill();
    }

    // the editor's documents: the team's emblem and each open drawing keep their draft and history apart
    private sealed record Session(Emblem Draft, List<Emblem> Undo, List<Emblem> Redo, int Selected);

    private static readonly Dictionary<string, Session> _sessions = [];
    private static string _session = "";
    private static Drawing? _doc;
    private static string? _docId;
    private static System.Action? _closeDoc;
    private static System.Action<string>? _forked;

    // Close was clicked once on a drawing with changes not saved: a second click closes it
    private static bool _closing;

    /// <summary>The document of that key in the editor: its own draft and history again, or a fresh one</summary>
    private static void Enter(string key, System.Func<Emblem> fresh)
    {
        if (_session == key) {
            return;
        }

        if (_session.Length > 0) {
            _sessions[_session] = new(_draft, _undo.ToList(), _redo.ToList(), _selected);
        }

        _session = key;
        _undo.Clear();
        _redo.Clear();
        if (_sessions.TryGetValue(key, out var s)) {
            (_draft, _selected) = (s.Draft, s.Selected);
            _undo.AddRange(s.Undo);
            _redo.AddRange(s.Redo);
        } else {
            _draft = fresh();
            _selected = 0;
        }

        _multi.Clear();
        _picking = 0;
        _colourField = NoColour;
        _note = null;
        _zoom = 0;
    }

    /// <summary>A drawing's history forgotten (closed without saving, deleted)</summary>
    internal static void Forget(string id) => _sessions.Remove("drawing:" + id);

    // a layer's largest size on this canvas (200 % of 64 pixels on an emblem, twice a drawing's longer side)
    private static int MaxLayerSize => Mathf.Max(TeamEmblem.MaxSize, TeamEmblem.MaxSize * Mathf.Max(CW, CH) / TeamEmblem.Grid);

    // the canvas' size (64 for an emblem)
    private static int CW => _draft.W;
    private static int CH => _draft.H;

    // the draft differs from what the team has, or from the drawing as saved (the Save button and its note)
    private static bool Unsaved => _doc != null
        ? TeamEmblem.LayerText(_draft) != TeamEmblem.LayerText(_doc.Art) || _draft.Clip != _doc.Art.Clip || _draft.W != _doc.Art.W || _draft.H != _doc.Art.H
        : TeamEmblem.Format(_draft) != TeamEmblem.Format(Saved(_team));

    private static Emblem Saved(int teamId) => TeamBanner.EmblemOf(teamId) ?? new([]);

    private static void Clear(YKLayout page)
    {
        // (hidden at once: Destroy waits for the frame's end, and the page laid out meanwhile still held the old rows
        // above the new ones, which pushed the page down under an empty half, lead dev 2026-11-02)
        foreach (Transform child in page.transform) {
            child.gameObject.SetActive(false);
            Object.Destroy(child.gameObject);
        }
    }

    private static bool HasSelection => _selected < _draft.Layers.Count;

    private static EmblemLayer Current => _draft.Layers[_selected];

    /// <summary>The selected layers (the primary one included), bottom first</summary>
    private static List<int> Selection()
    {
        var set = new HashSet<int>(_multi.Where(i => i < _draft.Layers.Count));
        if (HasSelection) {
            set.Add(_selected);
        }

        return set.OrderBy(i => i).ToList();
    }

    private static void Fill()
    {
        if (_left == null || _right == null) {
            return;
        }

        Clear(_left);
        Clear(_right);
        _dragKey = null;
        _moving = false;
        var left = _left;
        var right = _right;
        _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _draft.Layers.Count - 1));
        _multi.RemoveWhere(i => i >= _draft.Layers.Count);
        if (_draft.Layers.Count == 0) {
            _colourField = NoColour;
        }

        // the left page (lead dev 2026-11-02: "think of paint.net"): the tool bar and the two colours; the canvas in
        // its fixed square frame (the 64 grid at 3x, its pixels kept sharp, zoomed and scrolled inside the frame), and
        // beside it the emblem at the sizes it shows elsewhere (a player's card, a team's chip, a list row: user
        // 2026-10-30, 16 and 24 px 2026-11-01) or the drawing's credit, the history and Save; then the palette and
        // the tool's options, what is added, the whole emblem's buttons
        if (_leads) {
            ToolBar(left);
        }

        var top = left.Horizontal();
        top.Layout.childForceExpandWidth = false;
        top.Layout.childAlignment = TextAnchor.UpperLeft;
        top.Layout.spacing = 12f;
        top.LayoutElement().minHeight = top.LayoutElement().preferredHeight = FrameSize;
        top.LayoutElement().flexibleWidth = 1f;
        var sprite = PreviewSprite();
        var frame = Framed(top.transform, null, FrameSize, 0f, _zoom == 0);
        var preview = Canvas(frame.transform, sprite);
        var side = top.Vertical();
        side.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        side.Layout.spacing = 6f;
        side.Layout.childAlignment = TextAnchor.UpperLeft;
        side.LayoutElement().flexibleWidth = 1f;
        side.LayoutElement().preferredWidth = 0f;
        if (_doc != null) {
            // (the credit is always shown: who made it, whom it is based on; lead dev 2026-11-02)
            side.Text(_doc.Title.Length > 0 ? _doc.Title : "emp_ui_drawing_untitled".lang());
            side.TextSmall("emp_ui_drawing_by".Loc(_doc.CreatorName), FontColor.Gray);
            if (_doc.BasedOnName.Length > 0) {
                side.TextSmall("emp_ui_drawing_based_on".Loc(_doc.BasedOnName), FontColor.Gray);
            }

            side.TextSmall(CW + " x " + CH, FontColor.Gray);
        } else {
            var chips = side.Horizontal();
            chips.Layout.childForceExpandWidth = false;
            chips.Layout.childAlignment = TextAnchor.LowerLeft;
            chips.Layout.spacing = 10f;
            chips.LayoutElement().minHeight = chips.LayoutElement().preferredHeight = 54f;
            foreach (var size in new[] { 48f, 24f, 16f }) {
                Framed(chips.transform, sprite, size + 6f, size, false);
            }

        }

        if (!_leads) {
            right.TextSmall("emp_ui_emblem_layers".Loc(_draft.Layers.Count, TeamEmblem.MaxLayers), FontColor.Gray);
            var names = right.ListArea(_draft.Layers.Count, 10, 26f);
            for (var i = _draft.Layers.Count - 1; i >= 0; i--) {
                names.Text(NameOf(_draft.Layers[i]));
            }

            right.TextSmall("emp_ui_team_tools_leader_only".lang(), FontColor.Gray);
            Laid();
            return;
        }

        // a click on the canvas: the tool's (move picks the layer under the pointer and drags it; the paint tools draw)
        preview.raycastTarget = true;
        var input = preview.gameObject.AddComponent<EmblemPreviewInput>();
        (input.W, input.H) = (CW, CH);
        input.Down = PointerDown;
        input.Drag = PointerDrag;
        input.Up = PointerUp;
        input.Tick = Tick;

        // the history and Save beside the preview, the state of the draft under them
        var history = side.Horizontal();
        history.Layout.childForceExpandWidth = false;
        history.Layout.spacing = 6f;
        Half(history.Button("emp_ui_emblem_undo".lang(), Undo)).interactable = _undo.Count > 0;
        Half(history.Button("emp_ui_emblem_redo".lang(), Redo)).interactable = _redo.Count > 0;
        var length = _doc != null ? (_doc with { Art = _draft }).Format().Length : TeamEmblem.Format(_draft).Length;
        var limit = _doc != null ? Drawing.MaxText : TeamEmblem.MaxText;
        var fits = length <= limit;
        var saving = side.Horizontal();
        saving.Layout.childForceExpandWidth = false;
        saving.Layout.spacing = 6f;
        Half(saving.Button("emp_ui_emblem_save".lang(), Save)).interactable = (_draft.Layers.Count > 0 || _doc != null) && Unsaved && fits;
        if (_doc != null) {
            Half(saving.Button((_closing ? "emp_ui_drawing_close_sure" : "emp_ui_drawing_close").lang(), CloseDrawing));
            // (the saved drawing to the chat, its credit with it; greyed with the reason when it cannot go)
            var sharing = side.Horizontal();
            sharing.Layout.childForceExpandWidth = false;
            var send = Half(sharing.Button("emp_ui_drawing_send".lang(), () => {
                _note = ChatImages.SendDrawing(_doc) ?? "emp_ui_drawing_sent".lang();
                Fill();
            }));
            send.name = "DrawingSendToChat";
            var off = ChatImages.CantSend() ?? (Unsaved ? "emp_ui_drawing_send_save_first".lang() : null);
            send.interactable = off == null;
            if (off != null) {
                side.TextSmall(off, FontColor.Gray);
            }
        }

        if (!fits) {
            // (review 2026-11-01: the host refused a text past its limit without a word)
            side.TextSmall("emp_ui_emblem_too_long".Loc(length, limit), FontColor.Warning);
        } else if (Unsaved && (_draft.Layers.Count > 0 || _doc != null)) {
            side.TextSmall("emp_ui_emblem_unsaved".lang(), FontColor.Warning);
        }

        if (_note != null) {
            side.TextSmall(_note, FontColor.Gray);
            _note = null;
        }

        PaintOptions(left);

        // what is added: a shape, a picture, a text or a paint layer (equal buttons sharing the page's width)
        var adds = left.ButtonRow();
        var room = _draft.Layers.Count < TeamEmblem.MaxLayers;
        UIButton? shapes = null;
        shapes = Even(adds.RowButton("emp_ui_emblem_add_shape".lang(), () => ShowShapes(shapes)));
        shapes.interactable = room;
        Even(adds.RowButton("emp_ui_emblem_add_picture".lang(), () => OpenGrid(PickLayer))).interactable = room;
        Even(adds.RowButton("emp_ui_emblem_add_text".lang(), AddText)).interactable = room;
        Even(adds.RowButton("emp_ui_emblem_add_paint".lang(), () => AddPaintLayer())).interactable = room;

        // the whole emblem at once: a random one, a template (an emblem's), the emblem or the drawing as a code
        var more = left.ButtonRow();
        if (_doc == null) {
            Even(more.RowButton("emp_ui_emblem_random".lang(), () => {
                Change(RandomEmblem());
                Select(0);
                _colourField = NoColour;
                Fill();
            }));
            UIButton? starters = null;
            starters = Even(more.RowButton("emp_ui_emblem_templates".lang(), () => ShowStarters(starters)));
        }

        UIButton? code = null;
        code = Even(more.RowButton("emp_ui_emblem_code".lang(), () => ShowCode(code)));

        // cut to the bottom layer (how every emblem drew before shapes became layers)
        var clip = left.Row().Toggle("emp_ui_emblem_clip".lang(), _draft.Clip, on => {
            Change(_draft with { Clip = on });
            Refresh();
        });
        clip.LayoutElement().flexibleWidth = 1f;
        clip.AsRow();
        clip.SetTooltipLang("emp_ui_emblem_clip_tip");

        // the right page: the picture grid, the colour picker, or the layers and the selected layer's tools
        if (_picking != 0) {
            right.ButtonRow().RowButton("emp_ui_emblem_add_close".lang(), () => {
                _picking = 0;
                Fill();
            });
            var mode = _picking;
            // (the grid takes the height the page leaves: lead dev 2026-11-02, three rows over an empty half page)
            TeamBanner.BuildThingPicker(right, Dark, thing => Picked(mode, thing), fill: true);
        } else if (_colourField is Primary or Secondary) {
            BuildPaintColour(right);
        } else if (_colourField != NoColour && HasSelection) {
            BuildColour(right);
        } else {
            BuildLayers(right);
        }

        Laid();
    }

    /// <summary>
    ///     The pages laid out again by the tab (the buttons fitted to their labels, the texts wrapped, a list that
    ///     fills the page grown to it); only the window's content moves, never the window
    /// </summary>
    private static void Laid()
    {
        if (_left == null) {
            return;
        }

        if (_left.GetComponentInParent<TabEmpBase>() is { } tab) {
            tab.Relayout();
        } else if (_left.transform.parent is RectTransform spread) {
            LayoutRebuilder.MarkLayoutForRebuild(spread);
        }
    }

    private static void BuildLayers(YKLayout right)
    {
        // the title and the edit buttons on one line, over a list of a fixed height (four rows; it scrolls)
        var head = right.Row();
        head.Layout.spacing = 6f;
        var title = head.TextSmall("emp_ui_emblem_layers".Loc(_draft.Layers.Count, TeamEmblem.MaxLayers), FontColor.Gray);
        title.LayoutElement().flexibleWidth = 1f;
        title.LayoutElement().preferredWidth = 0f;
        Small(head.Button("emp_ui_emblem_copy".lang(), () => {
            if (HasSelection) {
                _clipboard = Current;
                Fill();
            }
        })).interactable = HasSelection;
        Small(head.Button("emp_ui_emblem_paste".lang(), Paste)).interactable = _clipboard != null && _draft.Layers.Count < TeamEmblem.MaxLayers;
        Small(head.Button("emp_ui_emblem_all".lang(), () => {
            _multi.Clear();
            _multi.UnionWith(Enumerable.Range(0, _draft.Layers.Count));
            Fill();
        })).interactable = _draft.Layers.Count > 1;

        if (_draft.Layers.Count == 0) {
            // (an empty list said nothing: how to start, user 2026-10-30 "on peut faire beaucoup mieux")
            right.EmptyText("emp_ui_emblem_start".lang());
            return;
        }

        var layers = right.ScrollList(ListRows * (LayerRowHeight + 2f));
        layers.Layout.spacing = 2f;
        // (the top layer first, as a stack is seen)
        for (var i = _draft.Layers.Count - 1; i >= 0; i--) {
            LayerRow(layers, i);
        }

        AlignRow(right);
        if (Current.Lock != 0) {
            right.TextSmall("emp_ui_emblem_locked".lang(), FontColor.Gray);
            return;
        }

        TeamBanner.Pills(right, ["emp_ui_emblem_tools_place".lang(), "emp_ui_emblem_tools_look".lang(), "emp_ui_emblem_tools_effects".lang()],
            _toolPage, i => {
                _toolPage = i;
                Fill();
            });
        // (the tools scroll in the height the page leaves: the window keeps its size whatever the layer has)
        var tools = right.ScrollList(120f, true);
        // (the game's texts start a few pixels left of their box: inside the clipped frame they lost their first letter)
        tools.Layout.padding = new(10, 14, 2, 4);
        switch (_toolPage) {
            case 0:
                PlaceTools(tools);
                break;
            case 1:
                LookTools(tools);
                break;
            default:
                EffectTools(tools);
                break;
        }
    }

    /// <summary>A button a share of the page's width (widened only by a longer label)</summary>
    private static UIButton Part(UIButton button, int of)
    {
        button.LayoutElement().preferredWidth = (PageWidth - 10f * (of - 1)) / of;
        return button;
    }

    /// <summary>A button sharing its row equally with the others (its label fitted inside, never widening the row)</summary>
    private static UIButton Even(UIButton button)
    {
        var element = button.LayoutElement();
        element.flexibleWidth = 1f;
        element.preferredWidth = 0f;
        element.minWidth = 40f;
        return button;
    }

    /// <summary>One of two buttons sharing a row equally</summary>
    private static UIButton Half(UIButton button)
    {
        var element = button.LayoutElement();
        element.flexibleWidth = 1f;
        element.preferredWidth = 0f;
        element.minWidth = 60f;
        return button;
    }

    /// <summary>A small button in a line (a list's Copy, Paste, All): as wide as its word, the line's height</summary>
    private static UIButton Small(UIButton button)
    {
        var element = button.LayoutElement();
        element.minWidth = 0f;
        element.preferredWidth = (button.mainText != null ? button.mainText.preferredWidth : 30f) + 28f;
        element.flexibleWidth = 0f;
        element.minHeight = element.preferredHeight = 28f;
        return button;
    }

    /// <summary>
    ///     The emblem at <paramref name="size" /> centred in a square frame of <paramref name="frame" /> (a thin border
    ///     in the page's ink over a slightly darker ground, nothing drawn past it); the big one also shows the centre
    ///     lines. Returns the emblem's image
    /// </summary>
    private static Image Framed(Transform parent, Sprite? sprite, float frame, float size, bool guides)
    {
        var box = new GameObject("EmblemFrame", typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(RectMask2D));
        box.transform.SetParent(parent, false);
        ((RectTransform)box.transform).sizeDelta = new(frame, frame);
        var element = box.GetComponent<LayoutElement>();
        (element.minWidth, element.preferredWidth, element.minHeight, element.preferredHeight, element.flexibleWidth) = (frame, frame, frame, frame, 0f);
        var ground = box.GetComponent<Image>();
        ground.color = new(0.28f, 0.22f, 0.13f, 0.10f);
        ground.raycastTarget = false;
        void Line(Vector2 min, Vector2 max, Vector2 thick, float alpha)
        {
            var line = new GameObject("Line", typeof(RectTransform), typeof(Image));
            line.transform.SetParent(box.transform, false);
            var lrt = (RectTransform)line.transform;
            (lrt.anchorMin, lrt.anchorMax, lrt.pivot) = (min, max, (min + max) / 2f);
            lrt.anchoredPosition = Vector2.zero;
            lrt.sizeDelta = thick;
            var image = line.GetComponent<Image>();
            image.color = new(0.28f, 0.22f, 0.13f, alpha);
            image.raycastTarget = false;
        }

        if (guides) {
            Line(new(0f, 0.5f), new(1f, 0.5f), new(0f, 1f), 0.12f);
            Line(new(0.5f, 0f), new(0.5f, 1f), new(1f, 0f), 0.12f);
        }

        // (the border inside the box: top, bottom, left, right)
        var edge = guides ? 2f : 1f;
        Line(new(0f, 1f), new(1f, 1f), new(0f, edge * 2f), 0.5f);
        Line(new(0f, 0f), new(1f, 0f), new(0f, edge * 2f), 0.5f);
        Line(new(0f, 0f), new(0f, 1f), new(edge * 2f, 0f), 0.5f);
        Line(new(1f, 0f), new(1f, 1f), new(edge * 2f, 0f), 0.5f);

        if (sprite == null) {
            // (the canvas' frame: the canvas is placed in it by the caller)
            return ground;
        }

        var emblem = TeamEmblemBaker.Place(box.transform, sprite, size);
        emblem.GetComponent<LayoutElement>().ignoreLayout = true;
        var rt = emblem.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        return emblem;
    }

    // ---------------------------------------------------------------- the editor's bakes

    /// <summary>The preview's sprite, owned by the editor and rewritten in place (the chips show the same one)</summary>
    private static Sprite PreviewSprite()
    {
        // (a canvas of another size gets a texture of its own: the old one is freed)
        var baked = TeamEmblemBaker.Into(_previewSprite, _draft);
        if (_previewSprite != null && baked != _previewSprite) {
            TeamEmblemBaker.Free(_previewSprite);
        }

        _previewSprite = baked;
        _dirty = false;
        _lastBake = Time.realtimeSinceStartup;
        return _previewSprite;
    }

    /// <summary>The layer alone, centred and full size (its thumbnail in the list), in the editor's small cache</summary>
    private static Sprite Thumb(EmblemLayer layer)
    {
        var alone = new Emblem([layer with { X = TeamEmblem.Grid / 2, Y = TeamEmblem.Grid / 2, Size = 100, Rotation = 0, Flip = 0, Opacity = 100, Hidden = 0, Sym = 0, ClipBelow = 0, Shadow = 0 }]);
        var key = TeamEmblem.Format(alone);
        if (_thumbs.TryGetValue(key, out var node) && node.Value.Sprite != null) {
            _thumbOrder.Remove(node);
            _thumbOrder.AddFirst(node);
            return node.Value.Sprite;
        }

        if (node != null) {
            _thumbOrder.Remove(node);
            _thumbs.Remove(key);
        }

        var sprite = TeamEmblemBaker.Into(null, alone);
        _thumbs[key] = _thumbOrder.AddFirst((key, sprite));
        while (_thumbOrder.Count > ThumbCapacity) {
            var last = _thumbOrder.Last!.Value;
            _thumbOrder.RemoveLast();
            _thumbs.Remove(last.Key);
            TeamEmblemBaker.Free(last.Sprite);
        }

        return sprite;
    }

    /// <summary>The draft changed: the preview is baked now, or at the next tick when one was baked under 50 ms ago</summary>
    private static void Refresh()
    {
        _dirty = true;
        if (Time.realtimeSinceStartup - _lastBake >= BakeEvery) {
            BakeNow();
        }
    }

    private static void BakeNow()
    {
        if (_previewSprite == null || _previewSprite.texture == null || _previewSprite.texture.width != CW || _previewSprite.texture.height != CH) {
            // (another canvas size: the next page build makes its texture)
            return;
        }

        TeamEmblemBaker.Into(_previewSprite, _draft);
        _dirty = false;
        _lastBake = Time.realtimeSinceStartup;
    }

    /// <summary>Each frame while the editor shows: a change waiting is baked once 50 ms have passed (the last one of a drag)</summary>
    private static void Tick()
    {
        if (_dirty && Time.realtimeSinceStartup - _lastBake >= BakeEvery) {
            BakeNow();
        }
    }

    // ---------------------------------------------------------------- the layer list

    /// <summary>A layer: its thumbnail and name (a click selects it, shift-click adds it), hide, lock, up, down, remove</summary>
    private static void LayerRow(YKLayout parent, int index)
    {
        var layer = _draft.Layers[index];
        var row = parent.Horizontal();
        row.Layout.childForceExpandWidth = false;
        row.Layout.childAlignment = TextAnchor.MiddleLeft;
        row.Layout.childControlHeight = true;
        row.Layout.childForceExpandHeight = false;
        row.Layout.spacing = 4f;
        row.Layout.padding = new(4, 4, 1, 1);
        row.LayoutElement().minHeight = row.LayoutElement().preferredHeight = LayerRowHeight;
        var picked = index == _selected || _multi.Contains(index);
        // (the whole row picks the layer, shaded as the journal's picked row; shift or ctrl adds it to the selection)
        EmpUi.Pickable(row, index == _selected, () => {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || Input.GetKey(KeyCode.LeftControl) ||
                Input.GetKey(KeyCode.RightControl)) {
                // (several layers, moved and aligned together)
                if (!_multi.Add(index)) {
                    _multi.Remove(index);
                }

                _multi.Add(_selected);
                if (_multi.Contains(index)) {
                    _selected = index;
                }
            } else {
                Select(index);
            }

            Fill();
        });
        if (picked && index != _selected) {
            row.GetComponent<Image>().color = new(0.28f, 0.22f, 0.13f, 0.07f);
        }

        TeamBanner.Swatch(row, new(0.28f, 0.22f, 0.13f, 0.12f), 26f, 26f, false, null);
        var swatch = row.transform.GetChild(row.transform.childCount - 1);
        var thumb = TeamEmblemBaker.Place(swatch, Thumb(layer), 24f);
        var trt = thumb.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new(0.5f, 0.5f);
        trt.anchoredPosition = Vector2.zero;
        if (layer.Hidden != 0) {
            thumb.color = new(1f, 1f, 1f, 0.35f);
        }

        var name = row.Text(NameOf(layer), layer.Hidden != 0 ? FontColor.Gray : FontColor.Default);
        name.LayoutElement().flexibleWidth = 1f;
        name.LayoutElement().preferredWidth = 0f;
        name.LayoutElement().minWidth = 40f;
        // (one line in the row; a long name is cut, never runs under the buttons)
        name.horizontalOverflow = HorizontalWrapMode.Wrap;
        name.verticalOverflow = VerticalWrapMode.Truncate;
        // the game's own small list buttons (its playlist's up and down, the inventory's trash), and an eye and a
        // lock drawn in their ink
        var icons = EmpUi.GameIcons;
        Icon(row.IconButton(layer.Hidden != 0 ? EditorIcons.EyeShut : EditorIcons.Eye, "o", () => Toggle(index, l => l with { Hidden = l.Hidden ^ 1 }), "emp_ui_emblem_hide", size: 24f));
        Icon(row.IconButton(layer.Lock != 0 ? EditorIcons.Lock : EditorIcons.Unlock, "u", () => Toggle(index, l => l with { Lock = l.Lock ^ 1 }), "emp_ui_emblem_lock", size: 24f));
        row.IconButton(icons?.up, "^", () => Move(index, 1), "emp_ui_emblem_up", index < _draft.Layers.Count - 1, size: 24f);
        row.IconButton(icons?.down, "v", () => Move(index, -1), "emp_ui_emblem_down", index > 0, size: 24f);
        row.IconButton(icons?.trash, "x", () => {
            Change(_draft with { Layers = _draft.Layers.Where((_, i) => i != index).ToList() });
            _multi.Clear();
            if (_selected >= index && _selected > 0) {
                _selected--;
            }

            Fill();
        }, "emp_ui_emblem_remove", layer.Lock == 0, size: 24f);
    }

    /// <summary>One layer changed from the list (hide, lock: allowed on a locked layer)</summary>
    private static void Toggle(int index, System.Func<EmblemLayer, EmblemLayer> change)
    {
        var list = _draft.Layers.ToList();
        list[index] = change(list[index]);
        Change(_draft with { Layers = list });
        Fill();
    }

    private static void Select(int index)
    {
        _selected = index;
        _multi.Clear();
    }

    /// <summary>
    ///     Align the selected layers' boxes (left, centres, right, top, middles, bottom) to the selection's bounds, or
    ///     a lone layer to the emblem's; spread three or more evenly across or down
    /// </summary>
    private static void AlignRow(YKLayout root)
    {
        var row = root.Row();
        row.Layout.spacing = 4f;
        var title = row.TextSmall("emp_ui_emblem_align".lang(), FontColor.Gray);
        title.LayoutElement().flexibleWidth = 1f;
        title.LayoutElement().preferredWidth = 0f;
        var count = Selection().Count;
        // (drawn icons with their hover text: the signs "|< T _ ^v" said nothing, lead dev 2026-11-02)
        foreach (var (tip, mode) in new[] {
                     ("emp_ui_emblem_al_left", 0), ("emp_ui_emblem_al_center", 1), ("emp_ui_emblem_al_right", 2),
                     ("emp_ui_emblem_al_top", 3), ("emp_ui_emblem_al_middle", 4), ("emp_ui_emblem_al_bottom", 5),
                 }) {
            var m = mode;
            Icon(row.IconButton(EditorIcons.Align(m), "-", () => Align(m), tip, count >= 1, size: 26f));
        }

        // (a gap between the aligns and the spreads)
        var gap = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
        gap.transform.SetParent(row.transform, false);
        gap.GetComponent<LayoutElement>().minWidth = gap.GetComponent<LayoutElement>().preferredWidth = 6f;
        Icon(row.IconButton(EditorIcons.Align(6), "<>", () => Spread(true), "emp_ui_emblem_dist_h", count >= 3, size: 26f));
        Icon(row.IconButton(EditorIcons.Align(7), "^v", () => Spread(false), "emp_ui_emblem_dist_v", count >= 3, size: 26f));
    }

    /// <summary>A drawn 16 px icon shown at its own size in the game's small button (never stretched or blurred)</summary>
    private static UIButton Icon(UIButton button)
    {
        if (button.icon != null) {
            button.icon.preserveAspect = true;
            var rt = button.icon.rectTransform;
            rt.anchorMin = rt.anchorMax = new(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new(16f, 16f);
        }

        return button;
    }

    private static float Half(EmblemLayer l) => TeamEmblem.Grid * l.Size / 200f;

    private static void Align(int mode)
    {
        var picked = Selection().Where(i => _draft.Layers[i].Lock == 0).ToList();
        if (picked.Count == 0) {
            return;
        }

        var ls = picked.Select(i => _draft.Layers[i]).ToList();
        // (one layer: the emblem's bounds; several: theirs)
        float minX = 0f, maxX = CW, minY = 0f, maxY = CH;
        if (picked.Count > 1) {
            (minX, maxX) = (ls.Min(l => l.X - Half(l)), ls.Max(l => l.X + Half(l)));
            (minY, maxY) = (ls.Min(l => l.Y - Half(l)), ls.Max(l => l.Y + Half(l)));
        }

        var list = _draft.Layers.ToList();
        foreach (var i in picked) {
            var l = list[i];
            var h = Half(l);
            var (x, y) = mode switch {
                0 => (minX + h, (float)l.Y),
                1 => ((minX + maxX) / 2f, l.Y),
                2 => (maxX - h, l.Y),
                3 => (l.X, minY + h),
                4 => (l.X, (minY + maxY) / 2f),
                _ => ((float)l.X, maxY - h),
            };
            list[i] = l with { X = PosX(x), Y = PosY(y) };
        }

        Change(_draft with { Layers = list });
        Fill();
    }

    private static void Spread(bool across)
    {
        var picked = Selection().Where(i => _draft.Layers[i].Lock == 0).OrderBy(i => across ? _draft.Layers[i].X : _draft.Layers[i].Y).ToList();
        if (picked.Count < 3) {
            return;
        }

        var list = _draft.Layers.ToList();
        float first = across ? list[picked[0]].X : list[picked[0]].Y;
        float last = across ? list[picked[picked.Count - 1]].X : list[picked[picked.Count - 1]].Y;
        for (var k = 1; k < picked.Count - 1; k++) {
            var at = across ? PosX(first + (last - first) * k / (picked.Count - 1)) : PosY(first + (last - first) * k / (picked.Count - 1));
            list[picked[k]] = across ? list[picked[k]] with { X = at } : list[picked[k]] with { Y = at };
        }

        Change(_draft with { Layers = list });
        Fill();
    }

    // a place on the canvas: up to half its size past its edges
    private static int PosX(float v) => Mathf.Clamp(Mathf.RoundToInt(v), -CW / 2, CW + CW / 2);

    private static int PosY(float v) => Mathf.Clamp(Mathf.RoundToInt(v), -CH / 2, CH + CH / 2);

    // ---------------------------------------------------------------- the tools

    /// <summary>The move pad: the four nudges as a 3x3 cross of small arrows (the whole selection moves)</summary>
    private static void MovePad(YKLayout parent)
    {
        var icons = EmpUi.GameIcons;
        var pad = parent.Vertical();
        pad.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        pad.Layout.spacing = 2f;
        pad.Layout.childForceExpandWidth = false;
        pad.LayoutElement().flexibleWidth = 0f;
        // (the game's up arrow turned: 90 points left, -90 right)
        var cells = new (string Sign, float Angle, int Dx, int Dy)?[] {
            null, ("^", 0f, 0, -1), null,
            ("<", 90f, -1, 0), null, (">", -90f, 1, 0),
            null, ("v", 180f, 0, 1), null,
        };
        YKHorizontal? line = null;
        for (var i = 0; i < cells.Length; i++) {
            if (i % 3 == 0) {
                line = pad.Horizontal();
                line.Layout.spacing = 2f;
                line.Layout.childForceExpandWidth = false;
                line.Layout.childControlWidth = line.Layout.childControlHeight = true;
                line.LayoutElement().minHeight = line.LayoutElement().preferredHeight = 28f;
            }

            if (cells[i] is not { } cell) {
                var gap = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
                gap.transform.SetParent(line!.transform, false);
                var le = gap.GetComponent<LayoutElement>();
                le.minWidth = le.preferredWidth = 28f;
                continue;
            }

            var (dx, dy) = (cell.Dx, cell.Dy);
            line!.IconButton(icons?.up, cell.Sign, () => MoveSelection(dx * Step, dy * Step), null, true, cell.Angle, 28f);
        }
    }

    /// <summary>The selected layer's place: nudges, turn, flips, size, angle, opacity, duplicate, mirror</summary>
    private static void PlaceTools(YKLayout root)
    {
        var layer = Current;

        // the move pad beside the turn and the flips (one block instead of three rows of wide buttons)
        var block = root.Horizontal();
        block.Layout.childForceExpandWidth = false;
        block.Layout.childAlignment = TextAnchor.MiddleLeft;
        block.Layout.spacing = 12f;
        MovePad(block);
        var turn = block.Vertical();
        turn.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        turn.Layout.spacing = 2f;
        turn.Layout.childForceExpandWidth = false;
        foreach (var (label, change) in new (string, System.Func<EmblemLayer, EmblemLayer>)[] {
                     ("emp_ui_emblem_rotate", l => l with { Rotation = (l.Rotation / 45 + 1) * 45 % 360 }),
                     ("emp_ui_emblem_flip_h", l => l with { Flip = l.Flip ^ 1 }),
                     ("emp_ui_emblem_flip_v", l => l with { Flip = l.Flip ^ 2 }),
                 }) {
            var edit = change;
            var button = turn.Button(label.lang(), () => {
                Edit(edit);
                Fill();
            });
            var element = button.LayoutElement();
            element.minHeight = element.preferredHeight = 28f;
            element.preferredWidth = UIHelper.ButtonWidth;
            element.flexibleWidth = 0f;
        }

        Stepper(root, "emp_ui_emblem_size", layer.Size, TeamEmblem.MinSize, MaxLayerSize, 10,
            (l, v) => l with { Size = v }, l => l.Size, " %");
        Stepper(root, "emp_ui_emblem_angle", layer.Rotation, 0, 359, 15,
            (l, v) => l with { Rotation = v }, l => l.Rotation, "°");
        Stepper(root, "emp_ui_emblem_opacity", layer.Opacity, TeamEmblem.MinOpacity, 100, 10,
            (l, v) => l with { Opacity = v }, l => l.Opacity, " %");

        var copies = root.ButtonRow();
        copies.RowButton("emp_ui_emblem_duplicate".lang(), Duplicate).interactable = _draft.Layers.Count < TeamEmblem.MaxLayers;
        copies.RowButton("emp_ui_emblem_mirror".lang(), Mirror).interactable = _draft.Layers.Count < TeamEmblem.MaxLayers;
    }

    /// <summary>
    ///     The selected layer's look: a text's text, font, alignment, spacing and curve; its colour (a picture's tint,
    ///     or its own colours); a shape's pattern and second colour, texture, points, height and thickness; then the
    ///     outline's width and colour
    /// </summary>
    private static void LookTools(YKLayout root)
    {
        var layer = Current;
        if (layer.IsText) {
            var line = root.Row();
            line.Text("emp_ui_emblem_text".lang());
            var field = line.InputText(layer.Text);
            field.field.characterLimit = TeamEmblem.MaxChars;
            field.LayoutElement().flexibleWidth = 1f;
            field.field.onValueChanged.AddListener(typed => {
                var clean = new string(typed.Where(c => !char.IsControl(c)).ToArray());
                if (clean.Length is > 0 and <= TeamEmblem.MaxChars) {
                    Edit(l => l with { Thing = "x:" + TeamEmblem.Hex(clean) }, "text");
                }
            });
            field.field.onEndEdit.AddListener(_ => _dragKey = null);

            var fonts = EmblemText.Fonts();
            var pick = root.Row();
            if (fonts.Count > 0) {
                Choice(pick, "emp_ui_emblem_font", fonts.ToArray(), Mathf.Max(0, fonts.IndexOf(layer.Font)), i => {
                    Edit(l => l with { Font = i == 0 ? "" : fonts[i] });
                    Fill();
                });
            }

            Choice(pick, "emp_ui_emblem_align", Labels("emp_ui_emblem_align_", 3), layer.Align, i => {
                Edit(l => l with { Align = i });
                Fill();
            });
            Stepper(root, "emp_ui_emblem_spacing", layer.Spacing, -8, 16, 1, (l, v) => l with { Spacing = v }, l => l.Spacing, " px");
            Stepper(root, "emp_ui_emblem_curve", layer.Curve, -180, 180, 15, (l, v) => l with { Curve = v }, l => l.Curve, "°");
        }

        var colours = root.Row();
        ColourButton(colours, layer.IsSprite ? "emp_ui_emblem_tint" : "emp_ui_emblem_colour1", layer.Color, Fill1, true);
        if (layer.IsSprite) {
            var none = colours.Button("emp_ui_emblem_tint_none".lang(), () => {
                Edit(l => l with { Color = TeamEmblem.Own });
                Fill();
            });
            none.LayoutElement().preferredWidth = 90f;
            none.LayoutElement().minHeight = none.LayoutElement().preferredHeight = 28f;
            none.LayoutElement().flexibleWidth = 0f;
            none.interactable = layer.Color != TeamEmblem.Own;
        } else if (layer.IsShape) {
            var pattern = root.Row();
            Choice(pattern, "emp_ui_emblem_pattern", Labels("emp_ui_emblem_pattern_", TeamEmblem.PatternCount), layer.Pattern, i => {
                Edit(l => l with { Pattern = i });
                Fill();
            });
            ColourButton(pattern, "emp_ui_emblem_colour2", layer.Color2, Fill2, layer.Pattern != 0);

            // a picture of the game tiled across the shape (a floor, a wall...), tinted by the fill
            var texture = root.Row();
            var tex = texture.Button(layer.Texture.Length > 0 ? "emp_ui_emblem_texture".lang() + ": " + EmblemSprites.NameOf(layer.Texture) : "emp_ui_emblem_texture".lang(),
                () => OpenGrid(PickTexture));
            tex.LayoutElement().flexibleWidth = 1f;
            tex.LayoutElement().minHeight = tex.LayoutElement().preferredHeight = 28f;
            var clear = texture.Button("emp_ui_emblem_texture_none".lang(), () => {
                Edit(l => l with { Texture = "" });
                Fill();
            });
            clear.LayoutElement().preferredWidth = 90f;
            clear.LayoutElement().minHeight = clear.LayoutElement().preferredHeight = 28f;
            clear.LayoutElement().flexibleWidth = 0f;
            clear.interactable = layer.Texture.Length > 0;

            var kind = layer.Kind;
            if (kind is "star" or "polygon") {
                Stepper(root, kind == "star" ? "emp_ui_emblem_points" : "emp_ui_emblem_sides", layer.Sides, 3, 12, 1,
                    (l, v) => l with { Sides = v }, l => l.Sides, "");
            }

            if (kind is "rounded" or "star" or "line" or "cross" or "ring") {
                Stepper(root, "emp_ui_emblem_thick", layer.Thick, 5, 90, 5, (l, v) => l with { Thick = v }, l => l.Thick, " %");
            }
        }

        if (!layer.IsSprite && layer.Kind != "line") {
            Stepper(root, "emp_ui_emblem_height", layer.Aspect, 10, 100, 10, (l, v) => l with { Aspect = v }, l => l.Aspect, " %");
        }

        Stepper(root, "emp_ui_emblem_outline", layer.Outline, 0, TeamEmblem.MaxOutline, 1,
            (l, v) => l with { Outline = v }, l => l.Outline, " px");
        ColourButton(root.Row(), "emp_ui_emblem_outline_colour", layer.OutlineColor, OutlineColour, true);
    }

    /// <summary>The selected layer's effects: name, live mirror, blend, clip to the layer below, shadow, gradient</summary>
    private static void EffectTools(YKLayout root)
    {
        var layer = Current;
        var naming = root.Row();
        naming.Text("emp_ui_emblem_name".lang());
        var name = naming.InputText(layer.Name);
        name.field.characterLimit = TeamEmblem.MaxName;
        name.LayoutElement().flexibleWidth = 1f;
        name.field.onEndEdit.AddListener(typed => {
            var clean = new string(typed.Where(c => !char.IsControl(c)).ToArray());
            if (clean.Length <= TeamEmblem.MaxName && clean != Current.Name) {
                Edit(l => l with { Name = clean });
                Fill();
            }
        });

        var modes = root.Row();
        Choice(modes, "emp_ui_emblem_sym", Labels("emp_ui_emblem_sym_", 4), layer.Sym, i => {
            Edit(l => l with { Sym = i });
            Fill();
        });
        Choice(modes, "emp_ui_emblem_blend", Labels("emp_ui_emblem_blend_", 3), layer.Blend, i => {
            Edit(l => l with { Blend = i });
            Fill();
        });

        var clipLine = root.Row();
        var clip = clipLine.Toggle("emp_ui_emblem_clip_below".lang(), layer.ClipBelow != 0, on => Edit(l => l with { ClipBelow = on ? 1 : 0 }));
        clip.LayoutElement().flexibleWidth = 1f;
        clip.AsRow();
        clip.interactable = _selected > 0;

        Stepper(root, "emp_ui_emblem_shadow", layer.Shadow, 0, TeamEmblem.MaxShadow, 1, (l, v) => l with { Shadow = v }, l => l.Shadow, " px");
        ColourButton(root.Row(), "emp_ui_emblem_shadow_colour", layer.ShadowColor, ShadowColour, layer.Shadow > 0);

        if (layer.IsSprite) {
            return;
        }

        var gradient = root.Row();
        Choice(gradient, "emp_ui_emblem_gradient", Labels("emp_ui_emblem_gradient_", 3), layer.Gradient, i => {
            Edit(l => l with { Gradient = i });
            Fill();
        });
        ColourButton(gradient, "emp_ui_emblem_grad_colour", layer.GradColor, GradColour, layer.Gradient != 0);
        if (layer.Gradient == 1) {
            Stepper(root, "emp_ui_emblem_grad_angle", layer.GradAngle, 0, 359, 15, (l, v) => l with { GradAngle = v }, l => l.GradAngle, "°");
        }
    }

    private static string[] Labels(string prefix, int count) => Enumerable.Range(0, count).Select(i => (prefix + i).lang()).ToArray();

    /// <summary>A small square of the colour and a button with its name: both open the colour picker for it</summary>
    private static void ColourButton(YKLayout row, string labelId, uint colour, int field, bool enabled)
    {
        void Open()
        {
            if (!enabled) {
                return;
            }

            _colourField = field;
            Fill();
        }

        var shown = field == Fill1 && colour == TeamEmblem.Own ? Color.white : ColourPicker.ToColor(colour);
        TeamBanner.Swatch(row, enabled ? shown : new(shown.r, shown.g, shown.b, 0.3f), 24f, 24f, false, Open);
        var button = row.Button(labelId.lang(), Open);
        button.LayoutElement().flexibleWidth = 1f;
        button.LayoutElement().minHeight = button.LayoutElement().preferredHeight = 28f;
        button.interactable = enabled;
    }

    /// <summary>The colour picker for the field being picked, with the layer's name and Done over it</summary>
    private static void BuildColour(YKLayout root)
    {
        var layer = Current;
        var head = root.Row();
        var title = _colourField switch {
            Fill2 => "emp_ui_emblem_colour2",
            OutlineColour => "emp_ui_emblem_outline_colour",
            ShadowColour => "emp_ui_emblem_shadow_colour",
            GradColour => "emp_ui_emblem_grad_colour",
            _ => layer.IsSprite ? "emp_ui_emblem_tint" : "emp_ui_emblem_colour1",
        };
        head.Text(NameOf(layer) + ": " + title.lang()).LayoutElement().flexibleWidth = 1f;
        var done = head.Button("emp_ui_colour_done".lang(), () => {
            _colourField = NoColour;
            Fill();
        });
        done.LayoutElement().preferredWidth = 100f;
        done.LayoutElement().flexibleWidth = 0f;

        var start = _colourField switch {
            Fill2 => layer.Color2,
            OutlineColour => layer.OutlineColor,
            ShadowColour => layer.ShadowColor,
            GradColour => layer.GradColor,
            _ => layer.Color == TeamEmblem.Own && layer.IsSprite ? 0xFFFFFFFFu : layer.Color,
        };
        var field = _colourField;
        ColourPicker.Build(root, start, TeamColours(), rgba => Edit(l => field switch {
            Fill2 => l with { Color2 = rgba },
            OutlineColour => l with { OutlineColor = rgba },
            ShadowColour => l with { ShadowColor = rgba },
            GradColour => l with { GradColor = rgba },
            // (a picture's tint keeps its alpha apart from "own colours": never the 0 that means them)
            _ => l with { Color = l.IsSprite && rgba == TeamEmblem.Own ? 0x00000001u : rgba },
        }, "colour"), _ => _dragKey = null);
    }

    /// <summary>The colour picker for the paint tools' primary or secondary colour</summary>
    private static void BuildPaintColour(YKLayout root)
    {
        var field = _colourField;
        var head = root.Row();
        head.Text((field == Primary ? "emp_ui_emblem_primary" : "emp_ui_emblem_secondary").lang()).LayoutElement().flexibleWidth = 1f;
        var done = head.Button("emp_ui_colour_done".lang(), () => {
            _colourField = NoColour;
            Fill();
        });
        done.LayoutElement().preferredWidth = 100f;
        done.LayoutElement().flexibleWidth = 0f;
        ColourPicker.Build(root, field == Primary ? _primary : _secondary, TeamColours(), rgba => {
            if (field == Primary) {
                _primary = rgba;
            } else {
                _secondary = rgba;
            }
        }, _ => { });
    }

    /// <summary>The team's colour, then every colour the draft already uses</summary>
    private static List<uint> TeamColours()
    {
        var list = new List<uint> { ColourPicker.ToRgba(TeamBanner.ChipColor(_team)) };
        foreach (var l in _draft.Layers) {
            if (l.Color != TeamEmblem.Own) {
                list.Add(l.Color);
            }

            if (l.IsShape && l.Pattern != 0) {
                list.Add(l.Color2);
            }

            if (l.Outline > 0) {
                list.Add(l.OutlineColor);
            }

            if (l.Gradient != 0) {
                list.Add(l.GradColor);
            }
        }

        return list.Distinct().Take(12).ToList();
    }

    /// <summary>A title in grey over one of the game's dropdowns, half the row wide</summary>
    private static void Choice(YKLayout row, string titleId, string[] options, int value, System.Action<int> change)
    {
        var col = row.Vertical();
        col.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        col.Layout.spacing = 0f;
        col.LayoutElement().flexibleWidth = 1f;
        col.LayoutElement().preferredWidth = 0f;
        col.TextSmall(titleId.lang(), FontColor.Gray);
        var dropdown = col.Dropdown(options.ToList(), change, Mathf.Clamp(value, 0, Mathf.Max(0, options.Length - 1)));
        dropdown.LayoutElement().flexibleWidth = 1f;
        dropdown.LayoutElement().preferredHeight = UIHelper.RowHeight;
        dropdown.Rect().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UIHelper.RowHeight);
    }

    /// <summary>A number of the selected layer: its title, "-", the game's slider, "+"</summary>
    private static void Stepper(YKLayout root, string titleId, int value, int min, int max, int step,
        System.Func<EmblemLayer, int, EmblemLayer> set, System.Func<EmblemLayer, int> get, string unit)
    {
        // its title in a column of its own, "-", the slider across the rest, "+", the value at the end (lead dev
        // 2026-11-02: the slider was a dot squeezed between "-" and "+" under its value)
        var row = root.Row();
        row.Layout.spacing = 6f;
        var title = row.Text(titleId.lang());
        var te = title.LayoutElement();
        (te.minWidth, te.preferredWidth, te.flexibleWidth) = (84f, 84f, 0f);
        Slider? slider = null;
        UIText? shown = null;
        void Show(int v)
        {
            if (shown != null) {
                shown.text = v + unit;
            }
        }

        row.SignButton("-", () => {
            Edit(l => set(l, Mathf.Clamp(get(l) - step, min, max)));
            slider?.SetValueWithoutNotify(get(Current));
            Show(get(Current));
        }, 28f);
        // (the game's SetSlider sets the range before the value: the prefab's value clamped to the new minimum fired
        // the callback while the row was built, an edit and an undo step nobody made; the bench's Undo then took back
        // that step instead of the player's, T3 2026-11-02)
        var built = false;
        slider = row.Slider(value, v => {
            var rounded = Mathf.RoundToInt(v);
            Show(rounded);
            if (!built || rounded == get(Current)) {
                return;
            }

            // (one undo step for the whole drag)
            Edit(l => set(l, rounded), titleId);
        }, min, max, v => Mathf.RoundToInt(v) + unit);
        built = true;
        Show(value);
        var se = slider.LayoutElement();
        (se.minWidth, se.preferredWidth, se.flexibleWidth) = (80f, 0f, 1f);
        se.minHeight = se.preferredHeight = 20f;
        // (the slider's own label sat over it, cut by the row above: the value is said at the row's end instead)
        if (slider.GetComponentInChildren<Text>(true) is { } label) {
            label.gameObject.SetActive(false);
        }

        row.SignButton("+", () => {
            Edit(l => set(l, Mathf.Clamp(get(l) + step, min, max)));
            slider.SetValueWithoutNotify(get(Current));
            Show(get(Current));
        }, 28f);
        shown = row.Text(value + unit);
        var ve = shown.LayoutElement();
        (ve.minWidth, ve.preferredWidth, ve.flexibleWidth) = (50f, 50f, 0f);
        shown.alignment = TextAnchor.MiddleRight;
    }

    /// <summary>
    ///     A copy of the selected layer on the other side of the emblem, turned the other way (a pair of wings, two
    ///     crossed swords): the two halves of a symmetric emblem in one click (user 2026-10-30). The Effects page's
    ///     Mirror keeps such a copy live instead
    /// </summary>
    private static void Mirror()
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers || !HasSelection) {
            return;
        }

        var layer = Current;
        Insert(layer with { X = CW - layer.X, Flip = layer.Flip ^ 1, Rotation = (360 - layer.Rotation) % 360 });
    }

    private static void Duplicate()
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers || !HasSelection) {
            return;
        }

        // the copy just above the original, a little aside so it shows
        var copy = Current;
        Insert(copy with { X = PosX(copy.X + 4), Y = PosY(copy.Y + 4) });
    }

    private static void Paste()
    {
        if (_clipboard is not { } layer || _draft.Layers.Count >= TeamEmblem.MaxLayers) {
            return;
        }

        Insert(layer with { Lock = 0 });
    }

    /// <summary>A layer just above the selected one, selected</summary>
    private static void Insert(EmblemLayer layer)
    {
        var list = _draft.Layers.ToList();
        var at = HasSelection ? _selected + 1 : list.Count;
        list.Insert(at, layer);
        Change(_draft with { Layers = list });
        Select(at);
        Fill();
    }

    private static void Move(int index, int by)
    {
        var to = index + by;
        if (to < 0 || to >= _draft.Layers.Count) {
            return;
        }

        var list = _draft.Layers.ToList();
        (list[index], list[to]) = (list[to], list[index]);
        Change(_draft with { Layers = list });
        _multi.Clear();
        if (_selected == index) {
            _selected = to;
        } else if (_selected == to) {
            _selected = index;
        }

        Fill();
    }

    /// <summary>The shapes in the game's context menu under the button; one is added over the others</summary>
    private static void ShowShapes(UIButton? anchor)
    {
        var menu = EClass.ui.CreateContextMenuInteraction();
        if (anchor != null) {
            menu.SetHighlightTarget(anchor);
        }

        foreach (var kind in TeamEmblem.ShapeKinds) {
            var k = kind;
            menu.AddButton(("emp_ui_emblem_" + k).lang(), () => AddShape(k));
        }

        menu.Show();
    }

    internal static void AddShape(string kind)
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
            return;
        }

        // the first shape is the emblem's ground, in the team's colour; a later one smaller, in white
        var first = _draft.Layers.Count == 0;
        var colour = first ? ColourPicker.ToRgba(TeamBanner.ChipColor(_team)) : TeamEmblem.Rgba(17);
        Add(TeamEmblem.Shape(kind, colour, first ? 90 : 50));
    }

    /// <summary>A text layer of the team's name (its first 16 characters), white with a dark outline, on the Look page</summary>
    internal static void AddText()
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
            return;
        }

        var name = new string(Teams.NameOf(_team).Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).Take(TeamEmblem.MaxChars).ToArray());
        Add(TeamEmblem.TextLayer(name.Length > 0 ? name : "ABC", TeamEmblem.Rgba(17)));
        _toolPage = 1;
        Fill();
    }

    private static void Add(EmblemLayer layer)
    {
        if (CW != TeamEmblem.Grid || CH != TeamEmblem.Grid) {
            // (on a drawing: in its middle, as large for it as it would be on an emblem)
            var scale = Mathf.Min(CW, CH) / (float)TeamEmblem.Grid;
            var top = Mathf.Max(TeamEmblem.MaxSize, TeamEmblem.MaxSize * Mathf.Max(CW, CH) / TeamEmblem.Grid);
            layer = layer with { X = CW / 2, Y = CH / 2, Size = Mathf.Clamp(Mathf.RoundToInt(layer.Size * scale), TeamEmblem.MinSize, top) };
        }

        Change(_draft with { Layers = [.._draft.Layers, layer] });
        Select(_draft.Layers.Count - 1);
        _picking = 0;
        _colourField = NoColour;
        Fill();
    }

    private static void OpenGrid(int mode)
    {
        _picking = mode;
        _colourField = NoColour;
        Fill();
    }

    /// <summary>A picture picked in the grid: a new layer, or the selected shape's texture</summary>
    private static void Picked(int mode, string thing)
    {
        if (mode == PickTexture) {
            _picking = 0;
            Edit(l => l.IsShape ? l with { Texture = thing } : l);
            Fill();
            return;
        }

        if (_draft.Layers.Count < TeamEmblem.MaxLayers) {
            Add(TeamEmblem.Layer(thing));
        }
    }

    // ---------------------------------------------------------------- the emblem as a code, saving

    private static void Save()
    {
        if (_doc != null && _docId != null) {
            SaveDrawing();
            return;
        }

        var text = TeamEmblem.Format(_draft);
        if (text.Length > TeamEmblem.MaxText) {
            Fill();
            return;
        }

        // (the team's colour: the bottom shape's when it is one of the twelve team colours; else it stays)
        var colour = TeamEmblem.TeamColour(_draft) ?? TeamBanner.Of(_team)?.Color ?? 0;
        Teams.Ask(TeamAsk.Banner, 0, colour, text);
    }

    /// <summary>
    ///     The open drawing saved to My drawings: in place when it is this player's, else as a new drawing by them
    ///     based on its creator (the other player's stays as it was); the editor goes on with the saved one
    /// </summary>
    private static void SaveDrawing()
    {
        if (_doc == null || _docId == null) {
            return;
        }

        var mine = _doc.Creator == Drawing.Me;
        var saved = _doc.EditedByMe(_draft);
        if (DrawingStore.Save(saved, mine ? _docId : null) is not { } id) {
            _note = "emp_ui_drawing_full".lang();
            Fill();
            return;
        }

        if (id != _docId) {
            // (the session follows the new drawing: its history goes on)
            _sessions.Remove(_session);
            _session = "drawing:" + id;
            _docId = id;
            // (the list's page follows the new drawing: it is the one open now)
            _forked?.Invoke(id);
        }

        _doc = saved;
        _note = "emp_ui_drawing_saved".lang();
        Fill();
    }

    /// <summary>Back to My drawings; what was not saved is forgotten (after a second click)</summary>
    private static void CloseDrawing()
    {
        // (changes not saved: a second click, the player's work is never lost by a slip)
        if (Unsaved && !_closing) {
            _closing = true;
            Fill();
            return;
        }

        _closing = false;
        if (_docId != null) {
            Forget(_docId);
            if (_session == "drawing:" + _docId) {
                _session = "";
            }
        }

        _doc = null;
        _docId = null;
        _closeDoc?.Invoke();
    }

    /// <summary>Copy code (the emblem's text to the clipboard, to share) and Paste code in the game's context menu</summary>
    private static void ShowCode(UIButton? anchor)
    {
        var menu = EClass.ui.CreateContextMenuInteraction();
        if (anchor != null) {
            menu.SetHighlightTarget(anchor);
        }

        menu.AddButton("emp_ui_emblem_code_copy".lang(), () => {
            ExportCode();
            Fill();
        });
        menu.AddButton("emp_ui_emblem_code_paste".lang(), () => {
            ImportCode(GUIUtility.systemCopyBuffer ?? "");
            Fill();
        });
        menu.Show();
    }

    /// <summary>The draft's text on the clipboard</summary>
    internal static string ExportCode()
    {
        var text = _doc != null ? (_doc with { Art = _draft }).Format() : TeamEmblem.Format(_draft);
        GUIUtility.systemCopyBuffer = text;
        _note = "emp_ui_emblem_code_copied".lang();
        return text;
    }

    /// <summary>A pasted code as the draft (one undo step), when it reads as an emblem this game can draw</summary>
    internal static bool ImportCode(string code)
    {
        code = code.Trim();
        // (a drawing's code brings its layers and its canvas; an emblem's its layers on the canvas there is)
        if (Drawing.Parse(code) is { } drawing && (_doc != null || drawing.Art is { W: TeamEmblem.Grid, H: TeamEmblem.Grid })) {
            code = TeamEmblem.Prefix3 + (drawing.Art.Clip ? "1" : "0") + "|" + TeamEmblem.LayerText(drawing.Art);
            if (_doc != null) {
                Change(drawing.Art);
                Select(drawing.Art.Layers.Count - 1);
                _colourField = NoColour;
                _picking = 0;
                return true;
            }
        }

        if (code.Length is 0 or > TeamEmblem.MaxText ||
            TeamEmblem.Validate(TeamEmblem.Parse(code) is { } parsed ? parsed with { W = CW, H = CH } : null, TeamBanner.Normalize) is not { } emblem ||
            emblem.Layers.Count == 0) {
            _note = "emp_ui_emblem_code_bad".lang();
            return false;
        }

        Change(emblem);
        Select(emblem.Layers.Count - 1);
        _colourField = NoColour;
        _picking = 0;
        return true;
    }

    // ---------------------------------------------------------------- the preview's pointer

    /// <summary>The topmost visible, unlocked layer whose square holds this point of the grid, or -1</summary>
    private static int LayerAt(Vector2 p)
    {
        for (var i = _draft.Layers.Count - 1; i >= 0; i--) {
            var l = _draft.Layers[i];
            if (l.Hidden == 0 && l.Lock == 0 && Holds(l, p)) {
                return i;
            }
        }

        return -1;
    }

    private static bool Holds(EmblemLayer l, Vector2 p)
    {
        var half = Half(l);
        return Mathf.Abs(p.x - l.X) <= half && Mathf.Abs(p.y - l.Y) <= half;
    }

    private static void PointerDown(Vector2 p, bool right)
    {
        if (_tool != ToolMove) {
            PaintDown(p, right);
            return;
        }

        // (the selected layer keeps the pointer while it is under it, even under another one)
        var keep = HasSelection && Current.Lock == 0 && Holds(Current, p);
        var hit = keep ? _selected : LayerAt(p);
        _moving = hit >= 0;
        if (!_moving) {
            return;
        }

        if (hit != _selected && !_multi.Contains(hit)) {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) {
                _multi.Add(_selected);
                _multi.Add(hit);
            } else {
                _multi.Clear();
            }

            _selected = hit;
            _reselect = true;
        } else if (hit != _selected) {
            _selected = hit;
            _reselect = true;
        }

        var layer = _draft.Layers[hit];
        _grab = p - new Vector2(layer.X, layer.Y);
    }

    private static void PointerDrag(Vector2 p)
    {
        if (_painting) {
            PaintDrag(p);
            return;
        }

        if (!_moving || !HasSelection) {
            return;
        }

        var x = p.x - _grab.x;
        var y = p.y - _grab.y;
        if (_snap) {
            (x, y) = (Snap(x, CW / 2f), Snap(y, CH / 2f));
        }

        var l = Current;
        var (dx, dy) = (PosX(x) - l.X, PosY(y) - l.Y);
        if (dx != 0 || dy != 0) {
            MoveSelection(dx, dy, "preview");
        }
    }

    /// <summary>A position on the 4 px grid, or the centre when within 3 of it</summary>
    private static float Snap(float v, float centre)
    {
        return Mathf.Abs(v - centre) <= 3f ? centre : Mathf.Round(v / SnapGrid) * SnapGrid;
    }

    private static void PointerUp()
    {
        if (_painting) {
            PaintUp();
        }

        _moving = false;
        _dragKey = null;
        // (the drag's last position baked at once)
        BakeNow();
        if (_reselect) {
            _reselect = false;
            // (the layer list and the tools show the layer picked on the preview)
            Fill();
        }
    }

    /// <summary>Every selected, unlocked layer moved by that much (a drag's moves one undo step)</summary>
    private static void MoveSelection(int dx, int dy, string? drag = null)
    {
        var undoStep = drag == null || drag != _dragKey;
        _dragKey = drag;
        var list = _draft.Layers.ToList();
        var moved = false;
        foreach (var i in Selection()) {
            if (list[i].Lock != 0) {
                continue;
            }

            list[i] = list[i] with { X = PosX(list[i].X + dx), Y = PosY(list[i].Y + dy) };
            moved = true;
        }

        if (moved) {
            Change(_draft with { Layers = list }, undoStep);
            Refresh();
        }
    }

    // ---------------------------------------------------------------- random and starters

    /// <summary>
    ///     A random emblem: a shape of two colours (sometimes a gradient), one or two things of the game or an icon over
    ///     it (some turned, tinted or outlined, the first sometimes reaching past the shape), sometimes a ring or a
    ///     star, sometimes the team's initials
    /// </summary>
    private static Emblem RandomEmblem()
    {
        var rng = new System.Random();
        var bg = rng.Next(TeamBanner.EmblemColorCount);
        var bg2 = (bg + 1 + rng.Next(TeamBanner.EmblemColorCount - 1)) % TeamBanner.EmblemColorCount;
        var kinds = new[] { "square", "round", "shield", "banner", "diamond", "hexagon", "rounded", "star", "polygon" };
        var ground = TeamEmblem.Shape(kinds[rng.Next(kinds.Length)], TeamEmblem.Rgba(bg), 80 + rng.Next(3) * 10) with {
            Pattern = rng.Next(TeamEmblem.PatternCount), Color2 = TeamEmblem.Rgba(bg2), Sides = 5 + rng.Next(4),
            Outline = rng.Next(2), OutlineColor = TeamEmblem.Rgba(16),
        };
        if (rng.Next(4) == 0) {
            ground = ground with { Pattern = 0, Gradient = 1 + rng.Next(2), GradColor = TeamEmblem.Rgba(bg2) };
        }

        var layers = new List<EmblemLayer> { ground };
        if (rng.Next(3) == 0) {
            layers.Add(TeamEmblem.Shape(rng.Next(2) == 0 ? "ring" : "star", TeamEmblem.Rgba(rng.Next(TeamBanner.EmblemColorCount)), 60 + rng.Next(30)));
        }

        var rows = EClass.sources.things.rows;
        for (var n = layers.Count + 1 + rng.Next(2); layers.Count < n;) {
            string? thing = null;
            if (rng.Next(4) == 0 && SpriteGrid.All(2) is { Count: > 0 } icons) {
                // (an icon of the game: an element, a skill)
                thing = icons[rng.Next(icons.Count)].Value;
            }

            for (var tries = 0; tries < 30 && thing == null; tries++) {
                var row = rows[rng.Next(rows.Count)];
                if (!string.IsNullOrEmpty(row.id) && TeamBanner.Normalize("t:" + row.id) is { } typed && TeamBanner.SpriteOf(typed) != null) {
                    thing = typed;
                }
            }

            if (thing == null) {
                break;
            }

            var first = layers.Count(l => l.IsSprite) == 0;
            layers.Add(new(thing, first ? 32 : 20 + rng.Next(24), first ? 32 : 20 + rng.Next(24),
                first ? 70 + rng.Next(60) : 40 + rng.Next(30), rng.Next(3) == 0 ? rng.Next(8) * 45 : 0, rng.Next(4) == 0 ? 1 : 0,
                rng.Next(2) == 0 ? TeamEmblem.Own : TeamEmblem.Rgba(rng.Next(TeamBanner.EmblemColorCount)), 100, rng.Next(2)));
        }

        if (rng.Next(4) == 0) {
            var initials = new string(Teams.NameOf(_team).Split(' ').Where(w => w.Length > 0 && !char.IsSurrogate(w[0])).Select(w => w[0]).Take(3).ToArray());
            if (initials.Length > 0) {
                layers.Add(TeamEmblem.TextLayer(initials.ToUpperInvariant(), TeamEmblem.Rgba(17)) with { Y = 50, Size = 50 });
            }
        }

        return new(layers, rng.Next(3) == 0);
    }

    private static Emblem Old(int bg, int shape, int pattern, int bg2, params TeamEmblem.OldLayer[] layers) =>
        TeamEmblem.FromLegacy(new(bg, shape, layers, pattern, bg2))!;

    /// <summary>
    ///     A few ready emblems: the four templates (classic shield, crest, banner, round badge, named), then some of
    ///     the game's own things and monsters (one the game does not have is left out)
    /// </summary>
    private static readonly (string? Label, Emblem Emblem)[] Starters = [
        ("emp_ui_emblem_tpl_shield", new([
            TeamEmblem.Shape("shield", TeamEmblem.Rgba(12), 92) with { Pattern = 1, Color2 = TeamEmblem.Rgba(17), Outline = 2, OutlineColor = TeamEmblem.Rgba(18) },
            new("t:sword", 32, 30, 80, 0, 0, TeamEmblem.Own, 100, 1),
        ])),
        ("emp_ui_emblem_tpl_crest", new([
            TeamEmblem.Shape("hexagon", TeamEmblem.Rgba(19), 90) with { Gradient = 1, GradColor = TeamEmblem.Rgba(16), Outline = 1, OutlineColor = TeamEmblem.Rgba(18) },
            TeamEmblem.Shape("star", TeamEmblem.Rgba(18), 46) with { Y = 26, Sides = 5, Outline = 1 },
            TeamEmblem.TextLayer("CREST", TeamEmblem.Rgba(17)) with { Y = 50, Size = 60, Shadow = 1 },
        ])),
        ("emp_ui_emblem_tpl_banner", new([
            TeamEmblem.Shape("banner", TeamEmblem.Rgba(13), 96) with { Pattern = 6, Color2 = TeamEmblem.Rgba(18), Outline = 1 },
            new("e:ele_eleFire", 32, 26, 50, 0, 0, TeamEmblem.Own, 100, 1),
        ])),
        ("emp_ui_emblem_tpl_badge", new([
            TeamEmblem.Shape("round", TeamEmblem.Rgba(14), 96) with { Gradient = 2, GradColor = TeamEmblem.Rgba(16), Outline = 2, OutlineColor = TeamEmblem.Rgba(18) },
            TeamEmblem.Shape("ring", TeamEmblem.Rgba(18), 84) with { Thick = 12 },
            TeamEmblem.TextLayer("TEAM", TeamEmblem.Rgba(17)) with { Y = 22, Size = 60, Curve = 120 },
            new("c:putty", 32, 38, 50, 0, 0, TeamEmblem.Own, 100, 1),
        ])),
        (null, Old(19, 1, 0, 19, new TeamEmblem.OldLayer("c:putty", 32, 34, 90, 0, 0, TeamEmblem.OwnColours, 100, 1))),
        (null, Old(13, 2, 1, 16, new TeamEmblem.OldLayer("t:mathammer", 32, 30, 110, 1, 0, 18, 100, 1))),
        (null, Old(12, 3, 5, 0, new TeamEmblem.OldLayer("t:torch", 32, 28, 100, 0, 0, TeamEmblem.OwnColours, 100, 1))),
        (null, Old(7, 4, 7, 18, new TeamEmblem.OldLayer("t:crystal_mana", 32, 32, 80, 0, 0, TeamEmblem.OwnColours, 100, 0))),
        (null, Old(14, 5, 6, 6, new TeamEmblem.OldLayer("c:hound", 32, 34, 90, 0, 0, TeamEmblem.OwnColours, 100, 1))),
        (null, Old(16, 0, 3, 17, new TeamEmblem.OldLayer("t:crystal_sun", 22, 22, 60, 0, 0, 18, 100, 0),
            new TeamEmblem.OldLayer("t:crystal_earth", 42, 42, 60, 0, 0, 2, 100, 0))),
        // (shapes as layers: a sword past a round ground, a star in a ring)
        (null, new([
            TeamEmblem.Shape("round", TeamEmblem.Rgba(12), 76) with { Outline = 2, OutlineColor = TeamEmblem.Rgba(18) },
            new("t:sword", 32, 32, 130, 315, 0, TeamEmblem.Own, 100, 1),
        ])),
        (null, new([
            TeamEmblem.Shape("ring", TeamEmblem.Rgba(18), 96),
            TeamEmblem.Shape("star", TeamEmblem.Rgba(13), 70) with { Outline = 1 },
        ])),
    ];

    /// <summary>The starters in the game's context menu under the button, by their name or that of their main picture</summary>
    private static void ShowStarters(UIButton? anchor)
    {
        var menu = EClass.ui.CreateContextMenuInteraction();
        if (anchor != null) {
            menu.SetHighlightTarget(anchor);
        }

        foreach (var (label, starter) in Starters) {
            var layers = starter.Layers.Where(l => !l.IsSprite || TeamBanner.Normalize(l.Thing) != null).ToList();
            if (!layers.Any(l => l.IsSprite) && starter.Layers.Any(l => l.IsSprite) && label == null) {
                continue;
            }

            // (a template's text: the team's name when it fits)
            var team = new string(Teams.NameOf(_team).Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).Take(TeamEmblem.MaxChars).ToArray());
            layers = layers.Select(l => l.IsText && team.Length > 0 ? l with { Thing = "x:" + TeamEmblem.Hex(team) } : l).ToList();
            var pick = starter with { Layers = layers };
            var main = layers.FirstOrDefault(l => l.IsSprite);
            menu.AddButton(label?.lang() ?? NameOf(main.Thing != null ? main : layers[layers.Count - 1]), () => {
                Change(pick);
                Select(0);
                _colourField = NoColour;
                Fill();
            });
        }

        menu.Show();
    }

    // ---------------------------------------------------------------- the draft

    /// <summary>
    ///     The selected layer changed: the draft and the preview only (the buttons stay as they are). A
    ///     <paramref name="drag" /> key makes its repeated changes one undo step. A locked layer does not change
    /// </summary>
    private static void Edit(System.Func<EmblemLayer, EmblemLayer> change, string? drag = null)
    {
        var undoStep = drag == null || drag != _dragKey;
        _dragKey = drag;
        if (!HasSelection || Current.Lock != 0) {
            return;
        }

        var list = _draft.Layers.ToList();
        list[_selected] = change(list[_selected]);
        if (list[_selected] == _draft.Layers[_selected]) {
            return;
        }

        Change(_draft with { Layers = list }, undoStep);
        Refresh();
    }

    private static void Change(Emblem next, bool undoStep = true)
    {
        if (undoStep) {
            _undo.Add(_draft);
            if (_undo.Count > UndoMax) {
                _undo.RemoveAt(0);
            }

            _redo.Clear();
        }

        _draft = next;
    }

    internal static void Undo()
    {
        if (_undo.Count == 0) {
            return;
        }

        _redo.Add(_draft);
        _draft = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        _multi.Clear();
        Fill();
    }

    internal static void Redo()
    {
        if (_redo.Count == 0) {
            return;
        }

        _undo.Add(_draft);
        _draft = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        _multi.Clear();
        Fill();
    }

    private static string NameOf(EmblemLayer layer) => layer.Name.Length > 0 ? layer.Name : NameOf(layer.Thing);

    private static string NameOf(string thing)
    {
        if (thing.StartsWith("g:")) {
            return ("emp_ui_emblem_" + thing.Substring(2)).lang();
        }

        return EmblemSprites.NameOf(thing);
    }

    // ---------------------------------------------------------------- the bench (Debug eval)

    /// <summary>The draft's canonical text (the bench reads it)</summary>
    internal static string DraftText => TeamEmblem.Format(_draft);

    /// <summary>The steps that Undo and Redo hold (the bench reads them)</summary>
    internal static string History => _undo.Count + "/" + _redo.Count;

    /// <summary>The bench sets the draft from a text, as Starters would</summary>
    internal static bool SetDraft(string text)
    {
        if (TeamEmblem.Validate(TeamEmblem.Parse(text), TeamBanner.Normalize) is not { } emblem) {
            return false;
        }

        Change(emblem);
        Select(emblem.Layers.Count - 1);
        _colourField = NoColour;
        Fill();
        return true;
    }
}

/// <summary>
///     The emblem preview's pointer, in points of the 64 grid from the top left: pressed, dragged, released; and a
///     tick each frame (the editor's throttled bake)
/// </summary>
internal sealed class EmblemPreviewInput : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IPointerUpHandler
{
    internal System.Action<Vector2, bool>? Down;

    // the canvas' size in pixels (64 for an emblem)
    internal int W = TeamEmblem.Grid, H = TeamEmblem.Grid;
    internal System.Action<Vector2>? Drag;
    internal System.Action? Up;
    internal System.Action? Tick;

    private Vector2 OnGrid(PointerEventData data)
    {
        var rt = (RectTransform)transform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, data.position, data.pressEventCamera, out var p);
        var r = rt.rect;
        return new((p.x - r.xMin) / Mathf.Max(1f, r.width) * W, (r.yMax - p.y) / Mathf.Max(1f, r.height) * H);
    }

    // (the middle button scrolls a zoomed canvas instead of drawing)
    private static bool Pans(PointerEventData data) => data.button == PointerEventData.InputButton.Middle;

    public void OnPointerDown(PointerEventData data)
    {
        if (!Pans(data)) {
            Down?.Invoke(OnGrid(data), data.button == PointerEventData.InputButton.Right);
        }
    }

    public void OnBeginDrag(PointerEventData data)
    {
    }

    public void OnDrag(PointerEventData data)
    {
        if (!Pans(data)) {
            Drag?.Invoke(OnGrid(data));
            return;
        }

        if (GetComponentInParent<ScrollRect>() is { content: { } content } scroll && scroll.horizontal) {
            // (screen pixels over the canvas' scale)
            content.anchoredPosition += data.delta / Mathf.Max(0.01f, content.lossyScale.x);
        }
    }

    public void OnPointerUp(PointerEventData data)
    {
        if (!Pans(data)) {
            Up?.Invoke();
        }
    }

    private void Update() => Tick?.Invoke();
}
