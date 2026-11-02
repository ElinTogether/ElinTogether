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
///     The drawing and emblem editor in a window of its own (lead dev 2026-11-02: the editor squeezed in the
///     multiplayer menu's tab was "frankly not good"; paint.net as the model). A game window the player moves by its
///     caption and resizes by its edges: the actions along the top (undo, redo, save, send, set as emblem, canvas size,
///     code, zoom, grid, snap), the tools down the left, the canvas in the middle (the wheel zooms where the pointer
///     is, the middle button or Space with the left one pans), the panel on the right (the tool's options, the
///     colours, the preview at the sizes it is shown, the layers and the selected layer's tools) and a status line
///     (the pixel under the pointer, the canvas, the size against the network's limit, what just happened). The same
///     window edits the team's emblem (64x64) and a drawing of My drawings (any size); closing it with changes not
///     saved asks first, and a draft is kept on disk every few seconds
/// </summary>
internal static partial class EmblemEditor
{
    private const float SideWidth = 372f;
    private const float AutosaveEvery = 8f;

    private static readonly float[] ZoomSteps = [0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 16f, 20f, 24f, 32f];

    private static TabDrawingEditor? _host;
    private static EditorCanvasView? _view;
    private static UIText? _zoomText;
    private static UIText? _pointerText;
    private static RectTransform? _sideContent;
    private static float _sideScroll;
    private static float _zoomF = 4f;
    private static Vector2 _pan;
    private static bool _autoFit = true;
    private static Vector2 _viewSize;

    // the panel shown in place of the layers: the canvas size
    private const int PanelNone = 0, PanelResize = 1;
    private static int _panel;
    private static int _newW, _newH, _anchor = 4;

    // the draft kept on disk, the saved state shown
    private static float _lastAutosave;
    private static bool _autosaveDirty;
    private static float _nextPoll;
    private static bool _shownUnsaved;

    /// <summary>The window is open on a document</summary>
    internal static bool IsOpen => _host != null;

    private static string TeamKey(int team) => "team:" + team + ":" + TeamEmblem.Hex(Teams.NameOf(team));

    private static int MyTeam => EClass.core.IsGameStarted && EClass.pc != null ? Teams.TeamOf(EClass.pc.uid) : Teams.Wanderers;

    // ---------------------------------------------------------------- opening

    /// <summary>The team's emblem in the editor's window (the Team tab's Emblem page)</summary>
    internal static void OpenTeam(int team)
    {
        if (team == Teams.Wanderers) {
            return;
        }

        // (the document shown until now: its last edits written to disk at once)
        Autosave(true);

        _team = team;
        _doc = null;
        _docId = null;
        Enter(TeamKey(team), () => Saved(team));
        Show();
    }

    /// <summary>A drawing of My drawings in the editor's window</summary>
    internal static void OpenDrawing(string id)
    {
        if (DrawingStore.Get(id) is not { } doc) {
            return;
        }

        // (the document shown until now: its last edits written to disk at once)
        Autosave(true);

        _team = MyTeam;
        _doc = doc;
        _docId = id;
        Enter("drawing:" + id, () => doc.Art);
        Show();
    }

    private static void Show()
    {
        if (LayerDrawingEditor.Instance is { } open) {
            if (EClass.ui.layers.LastOrDefault() == open) {
                Fill();
                return;
            }

            // (under the multiplayer menu: opened again on top, its documents kept in memory)
            EClass.ui.RemoveLayer(open);
        }

        YK.CreateLayer<LayerDrawingEditor, LayerCreationData>(new(""));
    }

    /// <summary>The window's body built in this tab (once the window is laid out)</summary>
    internal static void Attach(TabDrawingEditor host)
    {
        _host = host;
        _viewSize = Vector2.zero;
        Fill();
    }

    /// <summary>The window closed: the draft kept on disk when it has changes, the window's parts forgotten</summary>
    internal static void Detach()
    {
        if (_host != null && Unsaved) {
            Autosave(true);
        }

        _host = null;
        _view = null;
        _zoomText = null;
        _pointerText = null;
        _sideContent = null;
        _painting = _moving = _selecting = _selMoving = false;
    }

    /// <summary>Save refused from the close question (too long for the network): said in the status line</summary>
    internal static void SaveRefused()
    {
        var (length, limit) = TextSize();
        _note = length > limit ? "emp_ui_emblem_too_long".Loc(length, limit) : "emp_ui_team_tools_leader_only".lang();
        Fill();
    }

    /// <summary>A selection left outside a canvas whose size an undo changed is dropped</summary>
    private static void DropSelectionOutside()
    {
        if (_sel is { } s && (s.xMax > CW || s.yMax > CH)) {
            _sel = null;
        }
    }

    /// <summary>Changes not saved that closing would lose</summary>
    internal static bool UnsavedNow => _host != null && Unsaved && (_draft.Layers.Count > 0 || _doc != null);

    /// <summary>Save, from the close question: true when the window may close</summary>
    internal static bool SaveForClose()
    {
        if (!CanSave) {
            return false;
        }

        Save();
        return !Unsaved || _doc == null;
    }

    /// <summary>The changes thrown away (Don't save): the document's history and its draft on disk forgotten</summary>
    internal static void Discard()
    {
        DrawingDrafts.Drop(_session);
        _sessions.Remove(_session);
        _session = "";
        _undo.Clear();
        _redo.Clear();
    }

    private static bool CanSave
    {
        get {
            var (length, limit) = TextSize();
            return (_draft.Layers.Count > 0 || _doc != null) && Unsaved && length <= limit;
        }
    }

    private static (int Length, int Limit) TextSize() =>
        _doc != null ? ((_doc with { Art = _draft }).Format().Length, Drawing.MaxText) : (TeamEmblem.Format(_draft).Length, TeamEmblem.MaxText);

    /// <summary>A panel or the selection closed by Escape before the window is: true when something was closed</summary>
    internal static bool Back()
    {
        if (_picking != 0 || _colourField != NoColour || _panel != PanelNone) {
            _picking = 0;
            _colourField = NoColour;
            _panel = PanelNone;
            Fill();
            return true;
        }

        if (_sel != null) {
            Deselect();
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- the draft kept on disk

    /// <summary>A document opened fresh: the draft kept on disk for it, when it differs from what is saved</summary>
    private static void Recover(string key)
    {
        if (DrawingDrafts.Get(key) is not { } kept) {
            return;
        }

        var art = kept.Draft.Art;
        if (_doc == null) {
            art = art with { W = TeamEmblem.Grid, H = TeamEmblem.Grid };
            if (art.W != kept.Draft.Art.W || art.H != kept.Draft.Art.H || TeamEmblem.Validate(art, TeamBanner.Normalize) is not { } valid) {
                DrawingDrafts.Drop(key);
                return;
            }

            art = valid;
        }

        if (TeamEmblem.LayerText(art) == TeamEmblem.LayerText(_draft) && art.Clip == _draft.Clip && art.W == _draft.W && art.H == _draft.H) {
            DrawingDrafts.Drop(key);
            return;
        }

        // (one undo step back to the saved one)
        Change(art);
        var when = System.DateTimeOffset.FromUnixTimeSeconds(kept.Time).ToLocalTime();
        _note = "emp_ui_editor_recovered".Loc(when.ToString("yyyy-MM-dd HH:mm"));
    }

    private static void Autosave(bool now = false)
    {
        if (_session.Length == 0 || !now && (!_autosaveDirty || Time.realtimeSinceStartup - _lastAutosave < AutosaveEvery)) {
            return;
        }

        _autosaveDirty = false;
        _lastAutosave = Time.realtimeSinceStartup;
        if (!Unsaved) {
            DrawingDrafts.Drop(_session);
            return;
        }

        var doc = _doc ?? Drawing.New(TeamEmblem.Grid, TeamEmblem.Grid, "");
        DrawingDrafts.Put(_session, doc with { Art = _draft });
    }

    /// <summary>Each frame while the window shows: the throttled bake, the autosave, the saved state followed</summary>
    internal static void WindowTick()
    {
        Tick();
        Autosave();
        if (Time.realtimeSinceStartup < _nextPoll || _painting || _moving || _selMoving || _selecting || Typing) {
            return;
        }

        _nextPoll = Time.realtimeSinceStartup + 0.5f;
        // (the host took the team's emblem, or another window changed it: Save and the status follow)
        if (Unsaved != _shownUnsaved) {
            Fill();
        }
    }

    // ---------------------------------------------------------------- the window's body

    private static void Fill()
    {
        if (_host == null) {
            return;
        }

        if (_sideContent != null) {
            _sideScroll = _sideContent.anchoredPosition.y;
        }

        Clear(_host);
        _dragKey = null;
        _moving = false;
        // (the old canvas view is gone with its pointer: what it was doing ends here)
        _painting = _selecting = _selMoving = false;
        _canvas = _strokeBase = _lifted = _liftBase = null;
        _cover = null;
        _strokeClip = null;
        _view = null;
        _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _draft.Layers.Count - 1));
        _multi.RemoveWhere(i => i >= _draft.Layers.Count);
        if (_draft.Layers.Count == 0) {
            _colourField = NoColour;
        }

        _shownUnsaved = Unsaved;
        if (LayerDrawingEditor.Instance is { } layer && layer.Window.textCaption != null) {
            layer.Window.textCaption.text = _doc != null
                ? "emp_ui_editor_caption_drawing".Loc(_doc.Title.Length > 0 ? _doc.Title : "emp_ui_drawing_untitled".lang())
                : "emp_ui_editor_caption_emblem".Loc(Teams.NameOf(_team));
        }

        var sprite = PreviewSprite();
        TopBar(_host);
        var middle = _host.Horizontal();
        middle.Fitter.enabled = false;
        middle.Layout.childControlHeight = true;
        middle.Layout.childForceExpandHeight = true;
        middle.Layout.childAlignment = TextAnchor.UpperLeft;
        middle.Layout.spacing = 6f;
        var me = middle.LayoutElement();
        me.flexibleHeight = 1f;
        me.minHeight = 240f;
        me.flexibleWidth = 1f;
        ToolColumn(middle);
        CanvasArea(middle, sprite);
        SidePanel(middle, sprite);
        StatusBar(_host);
        _host.Relayout();
        if (_sideContent != null) {
            _sideContent.anchoredPosition = new(_sideContent.anchoredPosition.x, _sideScroll);
        }

        ApplyView();
        ShowSelection();
    }

    /// <summary>A button of the top bar or a panel, as wide as its word, with its hover text</summary>
    private static UIButton Act(YKLayout row, string labelId, System.Action act, string? tip = null, bool enabled = true, string? name = null)
    {
        var button = Small(row.Button(labelId.lang(), act));
        button.interactable = enabled;
        if (tip != null) {
            Tip(button, tip);
        }

        if (name != null) {
            button.name = name;
        }

        return button;
    }

    /// <summary>A hover text given as it is (a tool's name and its key)</summary>
    private static void Tip(UIButton button, string text)
    {
        button.tooltip.enable = true;
        button.tooltip.lang = null;
        button.tooltip.text = text;
    }

    private static void Gap(YKLayout row, float width, float flexible = 0f)
    {
        var gap = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
        gap.transform.SetParent(row.transform, false);
        var element = gap.GetComponent<LayoutElement>();
        element.minWidth = element.preferredWidth = width;
        element.flexibleWidth = flexible;
    }

    private static void TopBar(YKLayout root)
    {
        var bar = root.Horizontal();
        bar.Fitter.enabled = false;
        bar.Layout.childAlignment = TextAnchor.MiddleLeft;
        bar.Layout.spacing = 4f;
        var be = bar.LayoutElement();
        be.minHeight = be.preferredHeight = 32f;
        be.flexibleHeight = 0f;

        Act(bar, "emp_ui_emblem_undo", Undo, "Ctrl+Z", _undo.Count > 0, "EditorUndo");
        Act(bar, "emp_ui_emblem_redo", Redo, "Ctrl+Y", _redo.Count > 0, "EditorRedo");
        Gap(bar, 8f);
        var (length, limit) = TextSize();
        Act(bar, "emp_ui_emblem_save", Save, length > limit ? "emp_ui_emblem_too_long".Loc(length, limit) : "Ctrl+S", CanSave, "EditorSave");
        if (_doc != null) {
            // (the saved drawing to the chat, its credit with it; greyed with the reason when it cannot go)
            var off = ChatImages.CantSend() ?? (Unsaved ? "emp_ui_drawing_send_save_first".lang() : null);
            Act(bar, "emp_ui_drawing_send", SendToChat, off, off == null, "DrawingSendToChat");
            if (CanSetEmblem) {
                Act(bar, "emp_ui_editor_set_emblem", () => {
                    _note = SetAsEmblem() ?? "emp_ui_editor_emblem_sent".lang();
                    Fill();
                }, "emp_ui_editor_set_emblem_tip".lang(), true, "EditorSetEmblem");
            }

            Act(bar, "emp_ui_editor_canvas_size", () => {
                OpenResize();
                Fill();
            }, null, true, "EditorCanvasSize");
        } else {
            Act(bar, "emp_ui_emblem_random", () => {
                Change(RandomEmblem());
                Select(0);
                _colourField = NoColour;
                Fill();
            });
            UIButton? starters = null;
            starters = Act(bar, "emp_ui_emblem_templates", () => ShowStarters(starters));
        }

        UIButton? code = null;
        code = Act(bar, "emp_ui_emblem_code", () => ShowCode(code));

        Gap(bar, 4f, 1f);
        bar.SignButton("-", () => ZoomStep(-1), 26f).name = "EditorZoomOut";
        _zoomText = bar.Text("");
        var ze = _zoomText.LayoutElement();
        ze.minWidth = ze.preferredWidth = 56f;
        _zoomText.alignment = TextAnchor.MiddleCenter;
        bar.SignButton("+", () => ZoomStep(1), 26f).name = "EditorZoomIn";
        Act(bar, "emp_ui_editor_fit", Fit, "Ctrl+0", true, "EditorFit");
        Act(bar, "emp_ui_editor_actual", Actual, "Ctrl+1", true, "EditorActual");
        var grid = Icon(bar.IconButton(EditorIcons.Grid, "#", () => {
            _grid = !_grid;
            ApplyView();
            Fill();
        }, null, true, 0f, 28f));
        Tip(grid, "emp_ui_emblem_grid".lang() + " (Ctrl+G)");
        Highlight(grid, _grid);
        // the drag's snap to the 4 px grid and the centre
        var snap = Icon(bar.IconButton(EditorIcons.Magnet, "S", () => {
            _snap = !_snap;
            Fill();
        }, "emp_ui_emblem_snap", true, 0f, 28f));
        Highlight(snap, _snap);
    }

    /// <summary>The tools down the left (each with its key), the two colours and their swap under them</summary>
    private static void ToolColumn(YKHorizontal middle)
    {
        var column = middle.Vertical();
        column.Fitter.enabled = false;
        column.Layout.childControlHeight = false;
        column.Layout.childForceExpandWidth = false;
        column.Layout.childControlWidth = false;
        column.Layout.childAlignment = TextAnchor.UpperCenter;
        column.Layout.spacing = 2f;
        var ce = column.LayoutElement();
        ce.minWidth = ce.preferredWidth = 36f;
        ce.flexibleWidth = 0f;
        foreach (var t in ToolOrder) {
            var tool = t;
            var button = Icon(column.IconButton(EditorIcons.Tool(t), "?", () => {
                _tool = tool;
                Fill();
            }, null, true, 0f, 32f));
            button.name = "EditorTool" + tool;
            Tip(button, ToolTip(tool).lang() + " (" + ToolKeys[tool] + ")");
            Highlight(button, tool == _tool);
        }

        Gap(column, 0f);
        var pair = new GameObject("Colours", typeof(RectTransform), typeof(LayoutElement));
        pair.transform.SetParent(column.transform, false);
        var element = pair.GetComponent<LayoutElement>();
        element.minWidth = element.preferredWidth = 34f;
        element.minHeight = element.preferredHeight = 36f;
        ((RectTransform)pair.transform).sizeDelta = new(34f, 36f);
        ColourSquare(pair.transform, _secondary, new(12f, -14f), Secondary);
        ColourSquare(pair.transform, _primary, new(0f, 0f), Primary);
        var swap = Icon(column.IconButton(EditorIcons.Swap, "<>", SwapColours, null, true, 0f, 24f));
        Tip(swap, "emp_ui_emblem_swap".lang() + " (X)");
    }

    private static void SwapColours()
    {
        (_primary, _secondary) = (_secondary, _primary);
        Fill();
    }

    /// <summary>A colour square of the pair: framed in ink, a click picks its colour</summary>
    private static void ColourSquare(Transform parent, uint colour, Vector2 at, int field)
    {
        var go = new GameObject(field == Primary ? "Primary" : "Secondary", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new(0f, 1f);
        rt.anchoredPosition = at;
        rt.sizeDelta = new(22f, 22f);
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
        image.color = on ? new(0.28f, 0.22f, 0.13f, 0.28f) : new(0f, 0f, 0f, 0f);
        image.raycastTarget = false;
    }

    private static RectTransform Child(Transform parent, string name, params System.Type[] parts)
    {
        var go = new GameObject(name, new[] { typeof(RectTransform) }.Concat(parts).ToArray());
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    /// <summary>
    ///     The canvas in the middle: a darker ground, the canvas' checker, the picture, the pixel grid from 4x, the
    ///     selection's outline and the brush's outline; the pointer's events go to <see cref="EditorCanvasView" />
    /// </summary>
    private static void CanvasArea(YKHorizontal middle, Sprite sprite)
    {
        var view = Child(middle.transform, "CanvasView", typeof(LayoutElement), typeof(Image), typeof(RectMask2D));
        var element = view.GetComponent<LayoutElement>();
        element.flexibleWidth = 1f;
        element.minWidth = 200f;
        element.flexibleHeight = 1f;
        view.GetComponent<Image>().color = new(0.28f, 0.22f, 0.13f, 0.18f);

        var content = Child(view, "Content");
        content.anchorMin = content.anchorMax = content.pivot = new(0.5f, 0.5f);
        var ground = Child(content, "Ground", typeof(RawImage));
        Stretch(ground);
        var checker = ground.GetComponent<RawImage>();
        checker.texture = Checker();
        checker.uvRect = new(0f, 0f, CW / 8f, CH / 8f);
        checker.raycastTarget = false;

        var picture = Child(content, "Picture", typeof(Image));
        Stretch(picture);
        var image = picture.GetComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false;

        var grid = Child(content, "PixelGrid", typeof(RawImage));
        Stretch(grid);
        grid.GetComponent<RawImage>().raycastTarget = false;

        var input = view.gameObject.AddComponent<EditorCanvasView>();
        input.Content = content;
        input.Picture = picture;
        input.Grid = grid.GetComponent<RawImage>();
        input.Sel = Outline(content, "Selection", new(0.1f, 0.08f, 0.06f, 0.95f), new(1f, 1f, 1f, 0.95f));
        input.Brush = Outline(content, "BrushOutline", new(0.1f, 0.08f, 0.06f, 0.6f), new(1f, 1f, 1f, 0.6f));
        input.Brush.gameObject.SetActive(false);
        _view = input;
    }

    /// <summary>A rectangle's outline, a dark line outside a light one (seen on any colour), placed by its anchors</summary>
    private static RectTransform Outline(Transform parent, string name, Color outer, Color inner)
    {
        var box = Child(parent, name);
        Stretch(box);
        void Edge(Vector2 min, Vector2 max, Vector2 pivot, Vector2 size, Vector2 offset, Color colour)
        {
            var e = Child(box, "Edge", typeof(Image));
            (e.anchorMin, e.anchorMax, e.pivot, e.sizeDelta, e.anchoredPosition) = (min, max, pivot, size, offset);
            var image = e.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
        }

        foreach (var (inset, colour) in new[] { (0f, outer), (1f, inner) }) {
            Edge(new(0f, 1f), new(1f, 1f), new(0.5f, 1f), new(-inset * 2f, 1f), new(0f, -inset), colour);
            Edge(new(0f, 0f), new(1f, 0f), new(0.5f, 0f), new(-inset * 2f, 1f), new(0f, inset), colour);
            Edge(new(0f, 0f), new(0f, 1f), new(0f, 0.5f), new(1f, -inset * 2f), new(inset, 0f), colour);
            Edge(new(1f, 0f), new(1f, 1f), new(1f, 0.5f), new(1f, -inset * 2f), new(-inset, 0f), colour);
        }

        return box;
    }

    /// <summary>The panel on the right, scrolled in its column (its place kept when it is built again)</summary>
    private static void SidePanel(YKHorizontal middle, Sprite sprite)
    {
        var frame = Child(middle.transform, "Side", typeof(LayoutElement), typeof(RectMask2D), typeof(Image), typeof(ScrollRect));
        var element = frame.GetComponent<LayoutElement>();
        element.minWidth = element.preferredWidth = SideWidth;
        element.flexibleWidth = 0f;
        element.flexibleHeight = 1f;
        frame.GetComponent<Image>().color = new(0f, 0f, 0f, 0.001f);

        var side = middle.Vertical();
        side.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        side.Layout.spacing = UIHelper.RowSpacing;
        side.Layout.padding = new(6, 16, 2, 12);
        side.Layout.childAlignment = TextAnchor.UpperLeft;
        side.transform.SetParent(frame, false);
        var rt = (RectTransform)side.transform;
        rt.anchorMin = new(0f, 1f);
        rt.anchorMax = new(1f, 1f);
        rt.pivot = new(0.5f, 1f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        _sideContent = rt;

        var scroll = frame.GetComponent<ScrollRect>();
        scroll.content = rt;
        scroll.viewport = frame;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = 30f;

        if (_doc != null) {
            DocSection(side);
        }

        if (_picking != 0) {
            var head = side.Row();
            head.Text((_picking == PickTexture ? "emp_ui_emblem_texture" : "emp_ui_emblem_add_picture").lang()).LayoutElement().flexibleWidth = 1f;
            Act(head, "emp_ui_emblem_add_close", () => {
                _picking = 0;
                Fill();
            });
            var mode = _picking;
            TeamBanner.BuildThingPicker(side, Dark, thing => Picked(mode, thing));
            return;
        }

        if (_panel == PanelResize && _doc != null) {
            ResizePanel(side);
            return;
        }

        if (_colourField is Primary or Secondary) {
            BuildPaintColour(side);
            return;
        }

        if (_colourField != NoColour && HasSelection) {
            BuildColour(side);
            return;
        }

        ToolSection(side);
        ColourSection(side);
        PreviewSection(side, sprite);
        LayerSection(side);
    }

    private static void Head(YKLayout side, string text)
    {
        side.Divider();
        side.TextSmall(text, FontColor.Gray);
    }

    /// <summary>A drawing's title (renamed here), its credit and its size</summary>
    private static void DocSection(YKLayout side)
    {
        if (_doc == null || _docId == null) {
            return;
        }

        var row = side.Row();
        row.Layout.spacing = 6f;
        var title = row.Text("emp_ui_editor_title_field".lang());
        var te = title.LayoutElement();
        (te.minWidth, te.preferredWidth, te.flexibleWidth) = (84f, 84f, 0f);
        var field = row.InputText(_doc.Title);
        field.field.characterLimit = Drawing.MaxTitle;
        field.LayoutElement().flexibleWidth = 1f;
        field.name = "EditorTitle";
        var id = _docId;
        field.field.onEndEdit.AddListener(typed => {
            var clean = new string(typed.Where(c => !char.IsControl(c)).Take(Drawing.MaxTitle).ToArray());
            if (_doc == null || _docId != id || clean == _doc.Title) {
                return;
            }

            DrawingStore.Rename(id, clean);
            _doc = _doc with { Title = clean };
            DrawingsPage.Refresh();
            Fill();
        });

        // (the credit is always shown: who made it, whom it is based on; lead dev 2026-11-02)
        var credit = "emp_ui_drawing_by".Loc(_doc.CreatorName);
        if (_doc.BasedOnName.Length > 0) {
            credit += "  ·  " + "emp_ui_drawing_based_on".Loc(_doc.BasedOnName);
        }

        side.TextSmall(credit, FontColor.Gray);
    }

    /// <summary>The selected tool's name and options</summary>
    private static void ToolSection(YKLayout side)
    {
        side.TextSmall(ToolTip(_tool).lang() + " (" + ToolKeys[_tool] + ")", FontColor.Gray);
        if (Sized(_tool)) {
            Number(side, "emp_ui_editor_size_px", _brush, 1, MaxBrush, " px", v => _brush = v, "EditorBrushSize");
        }

        if (Soft(_tool)) {
            Number(side, "emp_ui_editor_hardness", _hardness, 0, 100, " %", v => _hardness = v, "EditorHardness");
        }

        if (SeeThrough(_tool)) {
            Number(side, "emp_ui_editor_opacity", _opacity, 1, 100, " %", v => _opacity = v, "EditorOpacity");
        }

        switch (_tool) {
            case ToolRect:
            case ToolEllipse:
                var filled = side.Row().Toggle("emp_ui_emblem_filled".lang(), _filled, on => _filled = on);
                filled.AsRow();
                break;
            case ToolSelect:
                side.TextSmall("emp_ui_editor_select_help".lang(), FontColor.Gray);
                var row = side.ButtonRow();
                Even(row.RowButton("emp_ui_editor_select_all".lang(), SelectAll));
                Even(row.RowButton("emp_ui_editor_deselect".lang(), Deselect)).interactable = _sel != null;
                var edit = side.ButtonRow();
                Even(edit.RowButton("emp_ui_emblem_copy".lang(), () => CopySelection())).interactable = HasSelection;
                Even(edit.RowButton("emp_ui_editor_cut".lang(), () => CopySelection(true))).interactable = HasSelection;
                Even(edit.RowButton("emp_ui_emblem_paste".lang(), PastePixels)).interactable = _pixLast && _pixClip != null || _clipboard != null;
                Even(edit.RowButton("emp_ui_editor_erase".lang(), DeleteSelection)).interactable = _sel != null && HasSelection && Current.IsPaint;
                if (_doc != null && _sel != null) {
                    var crop = side.ButtonRow();
                    Even(crop.RowButton("emp_ui_editor_crop".lang(), CropToSelection)).name = "EditorCrop";
                }

                break;
            case ToolPencil:
                side.TextSmall("emp_ui_editor_pencil_help".lang(), FontColor.Gray);
                break;
            case ToolPicker:
                side.TextSmall("emp_ui_editor_picker_help".lang(), FontColor.Gray);
                break;
            case ToolMove:
                side.TextSmall("emp_ui_editor_move_help".lang(), FontColor.Gray);
                break;
            case ToolText:
                side.TextSmall("emp_ui_editor_text_help".lang(), FontColor.Gray);
                break;
        }

        if (Paints(_tool)) {
            side.TextSmall("emp_ui_editor_paint_help".lang(), FontColor.Gray);
        }
    }

    /// <summary>A number of the editor (a tool's size, hardness, opacity): its title, "-", the game's slider, "+"</summary>
    private static void Number(YKLayout root, string titleId, int value, int min, int max, string unit, System.Action<int> set, string name)
    {
        var row = root.Row();
        row.Layout.spacing = 6f;
        row.name = name;
        var title = row.Text(titleId.lang());
        var te = title.LayoutElement();
        (te.minWidth, te.preferredWidth, te.flexibleWidth) = (84f, 84f, 0f);
        Slider? slider = null;
        UIText? shown = null;
        var now = value;
        // (the game's slider fires its callback with the prefab's value clamped while it is set up: not a change)
        var built = false;
        void Put(int v)
        {
            if (!built) {
                return;
            }

            now = Mathf.Clamp(v, min, max);
            set(now);
            slider?.SetValueWithoutNotify(now);
            if (shown != null) {
                shown.text = now + unit;
            }
        }

        row.SignButton("-", () => Put(now - 1), 28f);
        slider = row.Slider(value, v => Put(Mathf.RoundToInt(v)), min, max, v => Mathf.RoundToInt(v) + unit);
        var se = slider.LayoutElement();
        (se.minWidth, se.preferredWidth, se.flexibleWidth) = (80f, 0f, 1f);
        se.minHeight = se.preferredHeight = 20f;
        if (slider.GetComponentInChildren<Text>(true) is { } label) {
            label.gameObject.SetActive(false);
        }

        row.SignButton("+", () => Put(now + 1), 28f);
        built = true;
        shown = row.Text(value + unit);
        var ve = shown.LayoutElement();
        (ve.minWidth, ve.preferredWidth, ve.flexibleWidth) = (50f, 50f, 0f);
        shown.alignment = TextAnchor.MiddleRight;
    }

    /// <summary>The two colours, the palette (left click: primary, right click: secondary) and the recent colours</summary>
    private static void ColourSection(YKLayout side)
    {
        Head(side, "emp_ui_editor_colours".lang());
        var pair = side.Row();
        pair.Layout.spacing = 6f;
        foreach (var (field, colour, label) in new[] { (Primary, _primary, "emp_ui_emblem_primary"), (Secondary, _secondary, "emp_ui_emblem_secondary") }) {
            var f = field;
            void Open()
            {
                _colourField = f;
                Fill();
            }

            TeamBanner.Swatch(pair, ColourPicker.ToColor(colour), 28f, 24f, false, Open);
            var button = pair.Button(label.lang(), Open);
            button.LayoutElement().flexibleWidth = 1f;
            button.LayoutElement().preferredWidth = 0f;
            button.LayoutElement().minHeight = button.LayoutElement().preferredHeight = 28f;
        }

        Swatches(side, TeamBanner.Palette.Select(rgb => ((uint)rgb << 8) | 0xFFu).ToList(), "EditorPalette");
        if (ColourPicker.Recent.Count > 0) {
            side.TextSmall("emp_ui_colour_recent".lang(), FontColor.Gray);
            Swatches(side, ColourPicker.Recent.ToList(), "EditorRecent");
        }
    }

    /// <summary>Colours in rows of 16 squares: a left click sets the primary colour, a right click the secondary</summary>
    private static void Swatches(YKLayout side, List<uint> colours, string name)
    {
        var rows = (colours.Count + 15) / 16;
        var box = Child(side.transform, name, typeof(LayoutElement), typeof(GridLayoutGroup));
        var height = rows * 20f;
        var element = box.GetComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = height;
        element.flexibleWidth = 1f;
        box.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        var grid = box.GetComponent<GridLayoutGroup>();
        grid.cellSize = new(18f, 18f);
        grid.spacing = new(2f, 2f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 16;
        foreach (var rgba in colours) {
            var go = new GameObject("Swatch", typeof(RectTransform), typeof(Image), typeof(EventTrigger));
            go.transform.SetParent(box, false);
            go.GetComponent<Image>().color = ColourPicker.ToColor(rgba);
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            var colour = rgba;
            entry.callback.AddListener(data => {
                SE.ClickGeneral();
                if (data is PointerEventData { button: PointerEventData.InputButton.Right }) {
                    _secondary = colour;
                } else {
                    _primary = colour;
                }

                Fill();
            });
            go.GetComponent<EventTrigger>().triggers.Add(entry);
        }
    }

    /// <summary>
    ///     The picture at the sizes it is seen: an emblem at 64, 48, 24 and 16 pixels (a player's card, a team's chip,
    ///     a list row); a drawing as the chat shows it and as My drawings lists it
    /// </summary>
    private static void PreviewSection(YKLayout side, Sprite sprite)
    {
        Head(side, "emp_ui_editor_preview".lang());
        var row = side.Horizontal();
        row.Layout.childForceExpandWidth = false;
        row.Layout.childAlignment = TextAnchor.LowerLeft;
        row.Layout.spacing = 10f;
        float tallest;
        if (_doc == null) {
            foreach (var size in new[] { 64f, 48f, 24f, 16f }) {
                Framed(row.transform, sprite, size + 6f, size, false);
            }

            tallest = 70f;
        } else {
            // (as TabChat.AddDrawing scales it: whole steps up to 4x within 180 pixels)
            var scale = 180f / Mathf.Max(CW, CH);
            scale = scale >= 1f ? Mathf.Min(4f, Mathf.Floor(scale)) : scale;
            var chat = Mathf.Max(CW, CH) * scale;
            Framed(row.transform, sprite, chat + 6f, chat, false);
            Framed(row.transform, sprite, 46f, 40f, false);
            tallest = chat + 6f;
            side.TextSmall("emp_ui_editor_preview_drawing".lang(), FontColor.Gray);
        }

        row.LayoutElement().minHeight = row.LayoutElement().preferredHeight = tallest;
        ((RectTransform)row.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, tallest);
    }

    /// <summary>The layers: the list (top first), adding, duplicating, merging, removing; the selected layer's tools</summary>
    private static void LayerSection(YKLayout side)
    {
        side.Divider();
        var head = side.Row();
        head.Layout.spacing = 6f;
        var title = head.TextSmall("emp_ui_emblem_layers".Loc(_draft.Layers.Count, TeamEmblem.MaxLayers), FontColor.Gray);
        title.LayoutElement().flexibleWidth = 1f;
        title.LayoutElement().preferredWidth = 0f;
        Small(head.Button("emp_ui_emblem_copy".lang(), () => {
            if (HasSelection) {
                _clipboard = Current;
                _pixLast = false;
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
            side.EmptyText("emp_ui_editor_start".lang());
        } else {
            var rows = Mathf.Clamp(_draft.Layers.Count, 1, 6);
            var layers = side.ScrollList(rows * (LayerRowHeight + 2f));
            layers.Layout.spacing = 2f;
            // (the top layer first, as a stack is seen)
            for (var i = _draft.Layers.Count - 1; i >= 0; i--) {
                LayerRow(layers, i);
            }
        }

        // what is added: a paint layer, a shape, a picture, a text (equal buttons sharing the panel's width)
        var adds = side.ButtonRow();
        var room = _draft.Layers.Count < TeamEmblem.MaxLayers;
        Even(adds.RowButton("emp_ui_emblem_add_paint".lang(), () => AddPaintLayer())).interactable = room;
        UIButton? shapes = null;
        shapes = Even(adds.RowButton("emp_ui_emblem_add_shape".lang(), () => ShowShapes(shapes)));
        shapes.interactable = room;
        Even(adds.RowButton("emp_ui_emblem_add_picture".lang(), () => OpenGrid(PickLayer))).interactable = room;
        Even(adds.RowButton("emp_ui_emblem_add_text".lang(), AddText)).interactable = room;

        var ops = side.ButtonRow();
        var open = HasSelection && Current.Lock == 0;
        var dup = Even(ops.RowButton("emp_ui_emblem_duplicate".lang(), Duplicate));
        dup.interactable = HasSelection && room;
        dup.name = "EditorDuplicate";
        var merge = Even(ops.RowButton("emp_ui_editor_merge_down".lang(), MergeDown));
        merge.interactable = open && _selected > 0 && _draft.Layers[_selected - 1].Lock == 0;
        merge.name = "EditorMergeDown";
        var remove = Even(ops.RowButton("emp_ui_editor_delete_layer".lang(), () => RemoveLayer(_selected)));
        remove.interactable = open;
        remove.name = "EditorDeleteLayer";

        // cut to the bottom layer (how every emblem drew before shapes became layers)
        var clip = side.Row().Toggle("emp_ui_emblem_clip".lang(), _draft.Clip, on => {
            Change(_draft with { Clip = on });
            Refresh();
        });
        clip.LayoutElement().flexibleWidth = 1f;
        clip.AsRow();
        clip.SetTooltipLang("emp_ui_emblem_clip_tip");

        if (!HasSelection) {
            return;
        }

        side.Divider();
        if (Current.Lock != 0) {
            side.TextSmall("emp_ui_emblem_locked".lang(), FontColor.Gray);
            return;
        }

        // the selected layer's name and opacity, then its tools
        var naming = side.Row();
        naming.Layout.spacing = 6f;
        var label = naming.Text("emp_ui_emblem_name".lang());
        var le = label.LayoutElement();
        (le.minWidth, le.preferredWidth, le.flexibleWidth) = (84f, 84f, 0f);
        var name = naming.InputText(Current.Name);
        name.name = "EditorLayerName";
        name.field.characterLimit = TeamEmblem.MaxName;
        name.LayoutElement().flexibleWidth = 1f;
        name.field.onEndEdit.AddListener(typed => {
            var clean = new string(typed.Where(c => !char.IsControl(c)).ToArray());
            if (HasSelection && clean.Length <= TeamEmblem.MaxName && clean != Current.Name) {
                Edit(l => l with { Name = clean });
                Fill();
            }
        });
        Stepper(side, "emp_ui_emblem_opacity", Current.Opacity, TeamEmblem.MinOpacity, 100, 10,
            (l, v) => l with { Opacity = v }, l => l.Opacity, " %");
        AlignRow(side);
        TeamBanner.Pills(side, ["emp_ui_emblem_tools_place".lang(), "emp_ui_emblem_tools_look".lang(), "emp_ui_emblem_tools_effects".lang()],
            _toolPage, i => {
                _toolPage = i;
                Fill();
            });
        switch (_toolPage) {
            case 0:
                PlaceTools(side);
                break;
            case 1:
                LookTools(side);
                break;
            default:
                EffectTools(side);
                break;
        }
    }

    /// <summary>The status line: the pixel under the pointer, the canvas, the text against its limit, the news</summary>
    private static void StatusBar(YKLayout root)
    {
        var bar = root.Horizontal();
        bar.Fitter.enabled = false;
        bar.Layout.childAlignment = TextAnchor.MiddleLeft;
        bar.Layout.spacing = 16f;
        bar.Layout.childForceExpandWidth = false;
        var be = bar.LayoutElement();
        be.minHeight = be.preferredHeight = 22f;
        be.flexibleHeight = 0f;
        _pointerText = bar.TextSmall("", FontColor.Gray);
        var pe = _pointerText.LayoutElement();
        (pe.minWidth, pe.preferredWidth) = (70f, 70f);
        bar.TextSmall(CW + " x " + CH, FontColor.Gray);
        var (length, limit) = TextSize();
        bar.TextSmall("emp_ui_editor_text_size".Loc(length, limit), length > limit ? FontColor.Warning : FontColor.Gray).name = "EditorTextSize";

        var note = _note ?? (length > limit ? "emp_ui_emblem_too_long".Loc(length, limit)
            : _reduced ? "emp_ui_editor_reduced".lang()
            : Unsaved && (_draft.Layers.Count > 0 || _doc != null) ? "emp_ui_emblem_unsaved".lang()
            : _doc != null ? "emp_ui_editor_saved_state".lang() : "");
        var shown = bar.TextSmall(note, _note == null && Unsaved ? FontColor.Warning : FontColor.Default);
        shown.name = "EditorNote";
        shown.LayoutElement().flexibleWidth = 1f;
        shown.alignment = TextAnchor.MiddleRight;
        _note = null;
    }

    // ---------------------------------------------------------------- the view: zoom and pan

    private static float FitZoom(Vector2 view)
    {
        var z = Mathf.Min((view.x - 24f) / CW, (view.y - 24f) / CH);
        return z >= 1f ? Mathf.Floor(z) : Mathf.Max(0.25f, Mathf.Floor(z * 4f) / 4f);
    }

    /// <summary>The canvas' place and zoom in the view, the grid at it, the zoom's number</summary>
    internal static void ApplyView()
    {
        if (_zoomText != null) {
            _zoomText.text = Mathf.RoundToInt(_zoomF * 100f) + "%";
        }

        if (_view == null || _view.Content == null) {
            return;
        }

        var size = new Vector2(CW * _zoomF, CH * _zoomF);
        var room = _viewSize.x > 1f ? _viewSize : ((RectTransform)_view.transform).rect.size;
        var limit = (room + size) / 2f - new Vector2(24f, 24f);
        _pan = new(Mathf.Clamp(_pan.x, -Mathf.Max(0f, limit.x), Mathf.Max(0f, limit.x)), Mathf.Clamp(_pan.y, -Mathf.Max(0f, limit.y), Mathf.Max(0f, limit.y)));
        _view.Content.sizeDelta = size;
        _view.Content.anchoredPosition = new(Mathf.Round(_pan.x), Mathf.Round(_pan.y));
        if (_view.Grid != null) {
            var on = _grid && _zoomF >= 4f;
            _view.Grid.gameObject.SetActive(on);
            if (on) {
                _view.Grid.texture = GridTexture(Mathf.RoundToInt(_zoomF));
                _view.Grid.uvRect = new(0f, 0f, CW, CH);
            }
        }
    }

    /// <summary>The view's size changed (built, the window resized): the canvas fitted again unless the player zoomed</summary>
    internal static void ViewResized(Vector2 size)
    {
        _viewSize = size;
        if (_autoFit) {
            _zoomF = FitZoom(size);
            _pan = Vector2.zero;
        }

        ApplyView();
    }

    internal static void Fit()
    {
        _autoFit = true;
        if (_viewSize.x > 1f) {
            _zoomF = FitZoom(_viewSize);
        }

        _pan = Vector2.zero;
        ApplyView();
    }

    internal static void Actual()
    {
        _autoFit = false;
        _zoomF = 1f;
        _pan = Vector2.zero;
        ApplyView();
    }

    internal static void ZoomStep(int by) => ZoomAt(by, _pan);

    /// <summary>One zoom step in or out, the canvas' point under <paramref name="at" /> (the view's point) kept under it</summary>
    internal static void ZoomAt(int by, Vector2 at)
    {
        var next = by > 0 ? ZoomSteps.FirstOrDefault(z => z > _zoomF + 0.01f) : ZoomSteps.LastOrDefault(z => z < _zoomF - 0.01f);
        if (next <= 0f) {
            return;
        }

        var c = (at - _pan) / _zoomF;
        _zoomF = next;
        _pan = at - c * next;
        _autoFit = false;
        ApplyView();
    }

    internal static void PanBy(Vector2 delta)
    {
        _pan += delta;
        _autoFit = false;
        ApplyView();
    }

    /// <summary>A point of the screen in the canvas' pixels from its top left</summary>
    internal static Vector2 ScreenToCanvas(RectTransform picture, Vector2 screen, Camera? cam)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(picture, screen, cam, out var p);
        var r = picture.rect;
        return new((p.x - r.xMin) / Mathf.Max(1f, r.width) * CW, (r.yMax - p.y) / Mathf.Max(1f, r.height) * CH);
    }

    /// <summary>A pixel of the canvas on the screen (the bench's pointer)</summary>
    internal static Vector2 CanvasToScreen(float x, float y)
    {
        if (_view?.Picture is not { } picture) {
            return Vector2.zero;
        }

        var r = picture.rect;
        var local = new Vector2(r.xMin + x / CW * r.width, r.yMax - y / CH * r.height);
        return RectTransformUtility.WorldToScreenPoint(_view.Cam, picture.TransformPoint(local));
    }

    // ---------------------------------------------------------------- the pointer on the canvas

    internal static void CanvasDown(Vector2 p, bool right)
    {
        if (_tool == ToolSelect) {
            SelectDown(p);
            return;
        }

        PointerDown(p, right);
    }

    internal static void CanvasDrag(Vector2 p)
    {
        if (_tool == ToolSelect) {
            SelectDrag(p);
            return;
        }

        PointerDrag(p);
    }

    internal static void CanvasUp()
    {
        if (_tool == ToolSelect) {
            SelectUp();
            if (_reselect) {
                _reselect = false;
                Fill();
            }

            return;
        }

        PointerUp();
    }

    /// <summary>The pixel under the pointer said in the status line, the brush's outline drawn there</summary>
    internal static void Hover(Vector2? p)
    {
        var inside = p is { } q && q.x >= 0f && q.y >= 0f && q.x < CW && q.y < CH;
        if (_pointerText != null) {
            _pointerText.text = inside ? Mathf.FloorToInt(p!.Value.x) + ", " + Mathf.FloorToInt(p.Value.y) : "";
        }

        if (_view?.Brush is not { } brush) {
            return;
        }

        var show = inside && (Sized(_tool) || _tool is ToolPencil);
        brush.gameObject.SetActive(show);
        if (!show) {
            return;
        }

        var width = _tool == ToolPencil ? 1 : _brush;
        int cx = Mathf.FloorToInt(p!.Value.x), cy = Mathf.FloorToInt(p.Value.y);
        var x0 = width <= 2 ? cx : cx - (width - 1) / 2f;
        var y0 = width <= 2 ? cy : cy - (width - 1) / 2f;
        Place(brush, x0, y0, width, width);
    }

    /// <summary>A rectangle of the canvas' pixels placed by its anchors over the canvas</summary>
    private static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new(x / CW, 1f - (y + h) / CH);
        rt.anchorMax = new((x + w) / CW, 1f - y / CH);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static void ShowSelection()
    {
        if (_view?.Sel is not { } outline) {
            return;
        }

        outline.gameObject.SetActive(_sel != null);
        if (_sel is { } s) {
            Place(outline, s.x, s.y, s.width, s.height);
        }

        if (_pointerText != null && _sel is { } r && (_selecting || _selMoving)) {
            _pointerText.text = r.width + " x " + r.height;
        }
    }

    // ---------------------------------------------------------------- the keys

    private static readonly KeyCode[] Watched = [
        KeyCode.Z, KeyCode.Y, KeyCode.S, KeyCode.C, KeyCode.X, KeyCode.V, KeyCode.A, KeyCode.D, KeyCode.G, KeyCode.Alpha0,
        KeyCode.Keypad0, KeyCode.Alpha1, KeyCode.Keypad1, KeyCode.Equals, KeyCode.Plus, KeyCode.KeypadPlus, KeyCode.Minus,
        KeyCode.KeypadMinus, KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Delete, KeyCode.Backspace, KeyCode.M, KeyCode.P,
        KeyCode.B, KeyCode.E, KeyCode.L, KeyCode.R, KeyCode.O, KeyCode.I, KeyCode.T,
    ];

    private static readonly Queue<(KeyCode Key, bool Ctrl, bool Shift)> _keys = new();

    // a text field of the window (a title, a layer's name or text, a colour's hex) has the keyboard
    private static bool Typing => EventSystem.current?.currentSelectedGameObject?.GetComponent<InputField>() is { isFocused: true };

    /// <summary>The bench gives a key as a player presses it ("ctrl+z", "b", "]"): read in the same pass as the keyboard</summary>
    internal static void InjectKey(string combo)
    {
        var parts = combo.ToLowerInvariant().Split('+').Where(p => p.Length > 0).ToList();
        var ctrl = parts.Remove("ctrl");
        var shift = parts.Remove("shift");
        var name = parts.LastOrDefault() ?? "";
        var key = name switch {
            "[" => KeyCode.LeftBracket,
            "]" => KeyCode.RightBracket,
            "=" => KeyCode.Equals,
            "-" => KeyCode.Minus,
            "0" => KeyCode.Alpha0,
            "1" => KeyCode.Alpha1,
            "delete" => KeyCode.Delete,
            _ => (KeyCode)System.Enum.Parse(typeof(KeyCode), name, true),
        };
        _keys.Enqueue((key, ctrl, shift));
    }

    /// <summary>The window's keys, each frame while it is on top (never while a text field is being typed in)</summary>
    internal static void Keys()
    {
        if (_host == null) {
            return;
        }

        while (_keys.Count > 0) {
            var (key, ctrl, shift) = _keys.Dequeue();
            Key(key, ctrl, shift);
        }

        if (Typing) {
            return;
        }

        var c = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand) ||
                Input.GetKey(KeyCode.RightCommand);
        var s = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        foreach (var key in Watched) {
            if (Input.GetKeyDown(key)) {
                Key(key, c, s);
            }
        }
    }

    private static void Key(KeyCode key, bool ctrl, bool shift)
    {
        if (_painting || _moving || _selMoving) {
            return;
        }

        if (ctrl) {
            switch (key) {
                case KeyCode.Z when shift:
                case KeyCode.Y:
                    Redo();
                    return;
                case KeyCode.Z:
                    Undo();
                    return;
                case KeyCode.S:
                    if (CanSave) {
                        Save();
                    }

                    return;
                case KeyCode.C:
                    CopySelection();
                    return;
                case KeyCode.X:
                    CopySelection(true);
                    return;
                case KeyCode.V:
                    PastePixels();
                    return;
                case KeyCode.A:
                    SelectAll();
                    return;
                case KeyCode.D:
                    Deselect();
                    return;
                case KeyCode.G:
                    _grid = !_grid;
                    Fill();
                    return;
                case KeyCode.Alpha0:
                case KeyCode.Keypad0:
                    Fit();
                    return;
                case KeyCode.Alpha1:
                case KeyCode.Keypad1:
                    Actual();
                    return;
            }
        }

        switch (key) {
            case KeyCode.Equals:
            case KeyCode.Plus:
            case KeyCode.KeypadPlus:
                ZoomStep(1);
                return;
            case KeyCode.Minus:
            case KeyCode.KeypadMinus:
                ZoomStep(-1);
                return;
        }

        if (ctrl) {
            return;
        }

        switch (key) {
            case KeyCode.LeftBracket:
                _brush = Mathf.Max(1, _brush - (_brush > 10 ? 2 : 1));
                Fill();
                return;
            case KeyCode.RightBracket:
                _brush = Mathf.Min(MaxBrush, _brush + (_brush >= 10 ? 2 : 1));
                Fill();
                return;
            case KeyCode.Delete:
            case KeyCode.Backspace:
                DeleteSelection();
                return;
            case KeyCode.X:
                SwapColours();
                return;
        }

        foreach (var (tool, k) in ToolKeys) {
            if (k == key) {
                _tool = tool;
                Fill();
                return;
            }
        }
    }

    // ---------------------------------------------------------------- the layers

    private static void RemoveLayer(int index)
    {
        if (index < 0 || index >= _draft.Layers.Count || _draft.Layers[index].Lock != 0) {
            return;
        }

        Change(_draft with { Layers = _draft.Layers.Where((_, i) => i != index).ToList() });
        _multi.Clear();
        if (_selected >= index && _selected > 0) {
            _selected--;
        }

        Fill();
    }

    /// <summary>The selected layer and the one under it as one paint layer, as they are seen together</summary>
    internal static void MergeDown()
    {
        if (!HasSelection || _selected == 0 || Current.Lock != 0 || _draft.Layers[_selected - 1].Lock != 0) {
            return;
        }

        var below = _draft.Layers[_selected - 1];
        var px = TopDown(TeamEmblemBaker.Bake(new Emblem([below, Current], false, CW, CH)), CW, CH);
        if (PaintLayerOf(px) is not { } merged) {
            _note = "emp_ui_editor_merge_failed".lang();
            Fill();
            return;
        }

        var list = _draft.Layers.ToList();
        list[_selected - 1] = merged with { Name = below.Name };
        list.RemoveAt(_selected);
        Change(_draft with { Layers = list });
        Select(_selected - 1);
        Fill();
    }

    // ---------------------------------------------------------------- the canvas' size

    private static void OpenResize()
    {
        _panel = PanelResize;
        _newW = System.Array.IndexOf(Drawing.Sides, CW);
        _newH = System.Array.IndexOf(Drawing.Sides, CH);
        _anchor = 4;
    }

    /// <summary>The canvas' size: a width and a height, where the picture stays (nine places), Apply</summary>
    private static void ResizePanel(YKLayout side)
    {
        Head(side, "emp_ui_editor_canvas_size".lang());
        var sizes = Drawing.Sides.Select(s => s.ToString()).ToList();
        var row = side.Row();
        row.Layout.spacing = 6f;
        var wd = row.Dropdown(sizes, i => _newW = i, Mathf.Clamp(_newW, 0, sizes.Count - 1));
        wd.LayoutElement().preferredWidth = 90f;
        wd.LayoutElement().preferredHeight = UIHelper.RowHeight;
        wd.name = "EditorNewWidth";
        row.Text("x").LayoutElement().minWidth = 12f;
        var hd = row.Dropdown(sizes, i => _newH = i, Mathf.Clamp(_newH, 0, sizes.Count - 1));
        hd.LayoutElement().preferredWidth = 90f;
        hd.LayoutElement().preferredHeight = UIHelper.RowHeight;
        hd.name = "EditorNewHeight";

        side.TextSmall("emp_ui_editor_anchor".lang(), FontColor.Gray);
        for (var y = 0; y < 3; y++) {
            var line = side.Horizontal();
            line.Layout.childForceExpandWidth = false;
            line.Layout.spacing = 2f;
            line.LayoutElement().minHeight = line.LayoutElement().preferredHeight = 30f;
            for (var x = 0; x < 3; x++) {
                var at = y * 3 + x;
                var cell = line.SignButton(at == _anchor ? "■" : "·", () => {
                    _anchor = at;
                    Fill();
                }, 30f);
                cell.name = "EditorAnchor" + at;
                Highlight(cell, at == _anchor);
            }
        }

        var actions = side.ButtonRow();
        var apply = Even(actions.RowButton("emp_ui_editor_apply".lang(), () => {
            int w = Drawing.Sides[Mathf.Clamp(_newW, 0, Drawing.Sides.Length - 1)], h = Drawing.Sides[Mathf.Clamp(_newH, 0, Drawing.Sides.Length - 1)];
            int ax = _anchor % 3, ay = _anchor / 3;
            ResizeCanvas(w, h, ax == 0 ? 0 : ax == 1 ? (w - CW) / 2 : w - CW, ay == 0 ? 0 : ay == 1 ? (h - CH) / 2 : h - CH);
        }));
        apply.name = "EditorResizeApply";
        Even(actions.RowButton("emp_ui_editor_cancel".lang(), () => {
            _panel = PanelNone;
            Fill();
        }));
        if (_sel != null) {
            var crop = side.ButtonRow();
            Even(crop.RowButton("emp_ui_editor_crop".lang(), CropToSelection));
        }
    }

    private static int Side16(int v) => Mathf.Clamp((v + Drawing.SideStep - 1) / Drawing.SideStep * Drawing.SideStep, Drawing.MinSide, Drawing.MaxSide);

    /// <summary>The canvas cut to the selection (its sides rounded up to steps of 16)</summary>
    internal static void CropToSelection()
    {
        if (_sel is not { } s || _doc == null) {
            return;
        }

        ResizeCanvas(Side16(s.width), Side16(s.height), -s.x, -s.y);
    }

    /// <summary>
    ///     The drawing's canvas made w by h, the old picture placed at (ox, oy) in it: a paint layer that covered the
    ///     old canvas covers the new one (its pixels moved, cut where they leave it), every other layer moves by as
    ///     much. One undo step
    /// </summary>
    internal static void ResizeCanvas(int w, int h, int ox, int oy)
    {
        if (_doc == null || w < Drawing.MinSide || h < Drawing.MinSide || w > Drawing.MaxSide || h > Drawing.MaxSide ||
            w % Drawing.SideStep != 0 || h % Drawing.SideStep != 0 || w == CW && h == CH && ox == 0 && oy == 0) {
            _panel = PanelNone;
            Fill();
            return;
        }

        var full = 100 * Mathf.Max(CW, CH) / EmblemPaint.N;
        var top = Mathf.Max(TeamEmblem.MaxSize, TeamEmblem.MaxSize * Mathf.Max(w, h) / TeamEmblem.Grid);
        var list = new List<EmblemLayer>();
        foreach (var l in _draft.Layers) {
            if (l.IsPaint && l.X == CW / 2 && l.Y == CH / 2 && l.Rotation == 0 && l.Flip == 0 && l.Size == full &&
                EmblemPaint.Decode(l.Thing) is { } d && d.W == CW && d.H == CH && EmblemPaint.Layer(w, h) is { } cover) {
                var px = EmblemPaint.Blank(w, h);
                for (var y = 0; y < CH; y++) {
                    for (var x = 0; x < CW; x++) {
                        int tx = x + ox, ty = y + oy;
                        if (tx >= 0 && ty >= 0 && tx < w && ty < h) {
                            px[ty * w + tx] = d.Px[y * CW + x];
                        }
                    }
                }

                if ((EmblemPaint.Encode(px, w, h) ?? EmblemPaint.EncodeReduced(px, w, h)) is { } code) {
                    list.Add(l with { Thing = code, X = cover.X, Y = cover.Y, Size = cover.Size });
                    continue;
                }
            }

            list.Add(l with { X = Mathf.Clamp(l.X + ox, -w / 2, w + w / 2), Y = Mathf.Clamp(l.Y + oy, -h / 2, h + h / 2), Size = Mathf.Min(l.Size, top) });
        }

        Change(_draft with { Layers = list, W = w, H = h });
        _sel = null;
        _panel = PanelNone;
        _autoFit = true;
        Fill();
    }

    // ---------------------------------------------------------------- the drawing elsewhere

    private static void SendToChat()
    {
        if (_doc == null) {
            return;
        }

        _note = ChatImages.SendDrawing(_doc) ?? "emp_ui_drawing_sent".lang();
        Fill();
    }

    private static bool CanSetEmblem
    {
        get {
            if (_doc == null || !TeamBanner.On || !EClass.core.IsGameStarted || EClass.pc == null) {
                return false;
            }

            var team = MyTeam;
            return team != Teams.Wanderers && TeamRanks.Can(EClass.pc.uid, TeamPerm.Emblem, team);
        }
    }

    /// <summary>
    ///     The drawing as the team's emblem: its layers as they are when it is 64x64 and reads as an emblem, else the
    ///     picture as seen fitted into 64x64 as one paint layer. Null when asked of the host, else why not
    /// </summary>
    internal static string? SetAsEmblem()
    {
        if (!CanSetEmblem) {
            return "emp_ui_team_tools_leader_only".lang();
        }

        var team = MyTeam;
        Emblem? emblem = null;
        if (CW == TeamEmblem.Grid && CH == TeamEmblem.Grid) {
            emblem = TeamEmblem.Validate(_draft, TeamBanner.Normalize);
        }

        if (emblem == null || TeamEmblem.Format(emblem).Length > TeamEmblem.MaxText) {
            var seen = TopDown(TeamEmblemBaker.Bake(_draft with { W = CW, H = CH }), CW, CH);
            var px = Fit64(seen, CW, CH);
            if (EmblemPaint.Layer(TeamEmblem.Grid, TeamEmblem.Grid) is not { } layer ||
                (EmblemPaint.Encode(px) ?? EmblemPaint.EncodeReduced(px)) is not { } code) {
                return "emp_ui_editor_merge_failed".lang();
            }

            emblem = new Emblem([layer with { Thing = code }]);
        }

        var text = TeamEmblem.Format(emblem);
        if (text.Length > TeamEmblem.MaxText) {
            return "emp_ui_emblem_too_long".Loc(text.Length, TeamEmblem.MaxText);
        }

        var colour = TeamEmblem.TeamColour(emblem) ?? TeamBanner.Of(team)?.Color ?? 0;
        Teams.Ask(TeamAsk.Banner, 0, colour, text);
        // (the team's emblem opened next starts from the new one)
        _sessions.Remove(TeamKey(team));
        DrawingDrafts.Drop(TeamKey(team));
        return null;
    }

#if DEBUG
    // ---------------------------------------------------------------- the bench (Debug eval)

    /// <summary>
    ///     The window's state in one line for the bench: tool|zoom|pan|selection|layers|selected|WxH|unsaved|undo/redo|
    ///     brush|hardness|opacity|primary|secondary|grid|panel|picking|colourField|session
    /// </summary>
    internal static string BenchState =>
        string.Join("|", _tool, _zoomF.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            Mathf.RoundToInt(_pan.x) + "," + Mathf.RoundToInt(_pan.y), _sel is { } s ? s.x + "," + s.y + "," + s.width + "," + s.height : "-",
            _draft.Layers.Count, _selected, CW + "x" + CH, Unsaved, _undo.Count + "/" + _redo.Count, _brush, _hardness, _opacity,
            TeamEmblem.HexText(_primary), TeamEmblem.HexText(_secondary), _grid, _panel, _picking, _colourField, _session, _host != null);

    /// <summary>
    ///     A pointer event given to the canvas' own handler as the input module gives it: a press, a drag or a release
    ///     of a button (0 left, 1 right, 2 middle) at a pixel of the canvas, or a wheel step there
    /// </summary>
    internal static string BenchPointer(string kind, float x, float y, int button = 0, float wheel = 0f)
    {
        if (_view == null || EventSystem.current == null) {
            return "noview";
        }

        var data = new PointerEventData(EventSystem.current) {
            position = CanvasToScreen(x, y),
            button = (PointerEventData.InputButton)button,
            scrollDelta = new(0f, wheel),
        };
        data.pressPosition = data.position;
        var target = _view.gameObject;
        switch (kind) {
            case "down":
                ExecuteEvents.Execute(target, data, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
                break;
            case "drag":
                ExecuteEvents.Execute(target, data, ExecuteEvents.dragHandler);
                break;
            case "up":
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
                break;
            case "wheel":
                ExecuteEvents.Execute(target, data, ExecuteEvents.scrollHandler);
                break;
            default:
                return "nokind";
        }

        return "ok";
    }

    /// <summary>A stroke through those canvas pixels (x0 y0 x1 y1 ...): a press, drags, a release</summary>
    internal static string BenchStroke(string points, int button = 0)
    {
        var p = points.Split([' ', ','], System.StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (p.Count < 2) {
            return "nopoints";
        }

        var got = BenchPointer("down", p[0], p[1], button);
        for (var i = 2; i + 1 < p.Count; i += 2) {
            BenchPointer("drag", p[i], p[i + 1], button);
        }

        BenchPointer("up", p[p.Count - 2], p[p.Count - 1], button);
        return got;
    }

    /// <summary>The colour of a pixel of the canvas as baked now (#RRGGBBAA)</summary>
    internal static string BenchPixel(int x, int y)
    {
        var px = TeamEmblemBaker.Bake(_draft);
        return x < 0 || y < 0 || x >= CW || y >= CH ? "-" : TeamEmblem.HexText(ToRgba(px[(CH - 1 - y) * CW + x])) + (px[(CH - 1 - y) * CW + x].a == 255 ? "FF" : "");
    }

    internal static void BenchTool(int tool) => _tool = tool;

    internal static void BenchColours(string primary, string secondary)
    {
        if (TeamEmblem.TryHex(primary, out var a)) {
            _primary = a;
        }

        if (TeamEmblem.TryHex(secondary, out var b)) {
            _secondary = b;
        }
    }

    internal static void BenchBrush(int size, int hardness, int opacity)
    {
        (_brush, _hardness, _opacity) = (size, hardness, opacity);
    }
#endif

    /// <summary>A picture (rows top-down) shrunk or grown into 64x64, its shape kept, centred; each pixel the average of its box</summary>
    private static Color32[] Fit64(Color32[] px, int w, int h)
    {
        const int n = TeamEmblem.Grid;
        var scale = n / (float)Mathf.Max(w, h);
        int nw = Mathf.Max(1, Mathf.RoundToInt(w * scale)), nh = Mathf.Max(1, Mathf.RoundToInt(h * scale));
        int ox = (n - nw) / 2, oy = (n - nh) / 2;
        var output = new Color32[n * n];
        for (var y = 0; y < nh; y++) {
            for (var x = 0; x < nw; x++) {
                int x0 = Mathf.FloorToInt(x / scale), x1 = Mathf.Max(x0 + 1, Mathf.FloorToInt((x + 1) / scale));
                int y0 = Mathf.FloorToInt(y / scale), y1 = Mathf.Max(y0 + 1, Mathf.FloorToInt((y + 1) / scale));
                float r = 0, g = 0, b = 0, a = 0;
                var count = 0;
                for (var sy = y0; sy < Mathf.Min(h, y1); sy++) {
                    for (var sx = x0; sx < Mathf.Min(w, x1); sx++) {
                        var c = px[sy * w + sx];
                        var al = c.a / 255f;
                        (r, g, b, a) = (r + c.r * al, g + c.g * al, b + c.b * al, a + al);
                        count++;
                    }
                }

                if (count == 0 || a <= 0.001f) {
                    continue;
                }

                output[(oy + y) * n + ox + x] = new((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)Mathf.RoundToInt(a / count * 255f));
            }
        }

        return output;
    }
}

/// <summary>
///     The editor's window (<see cref="EmblemEditor" />): a game window of its own, moved by its caption and resized
///     by its edges (the game's own Window does both), its place and size kept while the game runs. Escape closes a
///     panel or the selection first; closing with changes not saved asks Save / Don't save / Cancel; a right click
///     never closes it (the right button paints the secondary colour)
/// </summary>
internal sealed class LayerDrawingEditor : YKLayer<LayerCreationData>
{
    private const float MinW = 900f, MinH = 560f;
    internal const string TabId = "emp_tab_drawing_editor";

    private static Vector2? _position;
    private static Vector2? _size;

    private TabDrawingEditor? _tab;
    private bool _force;

    internal static LayerDrawingEditor? Instance { get; private set; }

    public override string Title => "emp_ui_editor_title".lang();

    public override Rect Bound => new(Vector2.zero, StartSize());

    private static Vector2 ScreenSize()
    {
        var scaler = ui.canvasScaler.scaleFactor;
        return new Vector2(Screen.width, Screen.height) / Mathf.Max(0.01f, scaler);
    }

    private static Vector2 StartSize()
    {
        var screen = ScreenSize();
        var want = _size ?? new Vector2(1180f, 760f);
        return new(Mathf.Min(Mathf.Max(want.x, MinW), screen.x * 0.96f), Mathf.Min(Mathf.Max(want.y, MinH), screen.y * 0.94f));
    }

    public override void OnLayout()
    {
        Instance = this;
        _tab = CreateTab<TabDrawingEditor>("emp_ui_editor_title", TabId);
    }

    public override void OnAfterAddLayer()
    {
        base.OnAfterAddLayer();
        Window.SwitchContent(TabId);
        // (one page: the strip of tabs is not shown)
        if (Window.rectTab != null) {
            Window.rectTab.gameObject.SetActive(false);
        }

        foreach (var fitter in Window.GetComponents<ContentSizeFitter>()) {
            fitter.enabled = false;
        }

        if (ClassExtension.Find(Window, "Content View") is { } view) {
            foreach (var fitter in view.GetComponents<ContentSizeFitter>()) {
                fitter.enabled = false;
            }
        }

        Window.RectTransform.sizeDelta = Bound.size;
        if (_position is { } at) {
            Window.transform.localPosition = at;
        }

        // the game's Window resizes by its edges between bound's (x, y) and (width, height)
        var screen = ScreenSize();
        Window.setting.allowMove = true;
        Window.setting.allowResize = true;
        Window.setting.bound = new Rect(Mathf.Min(MinW, screen.x * 0.9f), Mathf.Min(MinH, screen.y * 0.9f), screen.x, screen.y);
        if (_tab != null) {
            _tab.Host();
            EmblemEditor.Attach(_tab);
        }
    }

    public override void OnUpdateInput() => EmblemEditor.Keys();

    // (the right button paints the secondary colour: it never closes the window)
    public override void OnRightClick()
    {
    }

    public override bool OnBack() => EmblemEditor.Back() || base.OnBack();

    public override void Close()
    {
        if (_force || !EmblemEditor.UnsavedNow) {
            base.Close();
            return;
        }

        Dialog.Choice("emp_ui_editor_close_ask", d => {
            d.AddButton("emp_ui_editor_close_save".lang(), () => {
                if (EmblemEditor.SaveForClose()) {
                    _force = true;
                    Close();
                } else {
                    EmblemEditor.SaveRefused();
                }
            });
            d.AddButton("emp_ui_editor_close_discard".lang(), () => {
                EmblemEditor.Discard();
                _force = true;
                Close();
            });
            d.AddButton("emp_ui_editor_cancel".lang());
        });
    }

    /// <summary>Closed without the question (the drawing it showed was deleted)</summary>
    internal static void CloseNow()
    {
        if (Instance is { } open) {
            open._force = true;
            open.Close();
        }
    }

    public override void OnKill()
    {
        if (Window != null) {
            _position = Window.transform.localPosition;
            _size = Window.RectTransform.sizeDelta;
        }

        EmblemEditor.Detach();
        Instance = null;
    }
}

/// <summary>The editor window's one page: its body fills the window (nothing scrolls it), built by the editor</summary>
internal sealed class TabDrawingEditor : TabEmpBase
{
    public override void OnLayout()
    {
    }

    /// <summary>The page taken out of its scroll view and stretched over the window's body</summary>
    internal void Host()
    {
        if (GetComponentInParent<ScrollRect>() is { } scroll) {
            var holder = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            transform.SetParent(holder, false);
            if (scroll.content != null && scroll.content != transform) {
                scroll.content.gameObject.SetActive(false);
            }

            foreach (var bar in scroll.GetComponentsInChildren<Scrollbar>(true)) {
                bar.gameObject.SetActive(false);
            }

            // (nothing scrolls the editor; the tab's page fitting finds no scroll view to fit)
            DestroyImmediate(scroll);
        }

        var rt = (RectTransform)transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new(0.5f, 0.5f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
        if (GetComponent<LayoutElement>() is { } element) {
            element.ignoreLayout = true;
        }

        foreach (var fitter in GetComponents<ContentSizeFitter>()) {
            fitter.enabled = false;
        }

        var group = GetComponent<VerticalLayoutGroup>();
        group.childControlHeight = true;
        group.childForceExpandHeight = false;
        group.childControlWidth = true;
        group.childForceExpandWidth = true;
        group.spacing = 4f;
        group.padding = new(10, 10, 4, 6);
    }
}

/// <summary>
///     The canvas' pointer: a press, a drag and a release of the left (primary colour) or right (secondary) button go
///     to the selected tool; the middle button, or Space with the left one, pans; the wheel zooms where the pointer is.
///     Each frame: the pixel under the pointer, the editor's tick, the view's size followed
/// </summary>
internal sealed class EditorCanvasView : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IPointerUpHandler,
    IScrollHandler, IPointerEnterHandler, IPointerExitHandler, IInitializePotentialDragHandler
{
    internal RectTransform? Content;
    internal RectTransform? Picture;
    internal RawImage? Grid;
    internal RectTransform? Sel;
    internal RectTransform? Brush;

    private bool _panning;
    private bool _pressed;
    private bool _hover;
    private Vector2 _panLast;
    private Vector2 _size;

    internal Camera? Cam
    {
        get {
            var canvas = GetComponentInParent<Canvas>()?.rootCanvas;
            return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }
    }

    private Vector2 Local(Vector2 screen)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform, screen, Cam, out var p);
        return p;
    }

    private Vector2 OnCanvas(Vector2 screen) => Picture != null ? EmblemEditor.ScreenToCanvas(Picture, screen, Cam) : Vector2.zero;

    public void OnInitializePotentialDrag(PointerEventData data) => data.useDragThreshold = false;

    public void OnPointerDown(PointerEventData data)
    {
        if (data.button == PointerEventData.InputButton.Middle || data.button == PointerEventData.InputButton.Left && Input.GetKey(KeyCode.Space)) {
            _panning = true;
            _panLast = Local(data.position);
            return;
        }

        _pressed = true;
        EmblemEditor.CanvasDown(OnCanvas(data.position), data.button == PointerEventData.InputButton.Right);
    }

    public void OnBeginDrag(PointerEventData data)
    {
    }

    public void OnDrag(PointerEventData data)
    {
        if (_panning) {
            var now = Local(data.position);
            EmblemEditor.PanBy(now - _panLast);
            _panLast = now;
            return;
        }

        if (_pressed) {
            EmblemEditor.CanvasDrag(OnCanvas(data.position));
        }
    }

    public void OnPointerUp(PointerEventData data)
    {
        if (_panning) {
            _panning = false;
            return;
        }

        if (_pressed) {
            _pressed = false;
            EmblemEditor.CanvasUp();
        }
    }

    public void OnScroll(PointerEventData data)
    {
        if (data.scrollDelta.y != 0f) {
            EmblemEditor.ZoomAt(data.scrollDelta.y > 0f ? 1 : -1, Local(data.position));
        }
    }

    public void OnPointerEnter(PointerEventData data) => _hover = true;

    private void Start() => _hover = RectTransformUtility.RectangleContainsScreenPoint((RectTransform)transform, Input.mousePosition, Cam);

    public void OnPointerExit(PointerEventData data)
    {
        _hover = false;
        EmblemEditor.Hover(null);
    }

    private void Update()
    {
        EmblemEditor.WindowTick();
        if (_hover) {
            EmblemEditor.Hover(OnCanvas(Input.mousePosition));
        }
    }

    private void LateUpdate()
    {
        var size = ((RectTransform)transform).rect.size;
        if (size.x > 1f && (size - _size).sqrMagnitude > 0.5f) {
            _size = size;
            EmblemEditor.ViewResized(size);
        }
    }
}
