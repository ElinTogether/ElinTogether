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
///     The leader's emblem creator on the Team tab's Emblem page (PLAN_team_emblem): a 3x preview where a click picks
///     the layer under the pointer and a drag moves it; whether everything is cut to the bottom layer; undo, reset,
///     save, a random emblem, a few starters, and the two ways to add a layer: a shape (lead dev 2026-11-01: the old
///     background shape is a layer like the others, so a sword may reach past it) or a picture of the game's things
///     and monsters (<see cref="SpriteGrid" />). On the right page the layers (select, up, down, remove) and the
///     selected layer's tools on two pills: Place (move pad, turns, flips, size, angle, opacity, duplicate, mirror)
///     and Look (its colours by the <see cref="ColourPicker" />, pattern, points, height, thickness, outline). The
///     draft stays in this game until Save asks the host
/// </summary>
internal static class EmblemEditor
{
    private const int UndoMax = 20;
    private const int Step = 2;
    private const float PreviewSize = TeamEmblem.Grid * 3f;

    // the colour being picked: none, the fill (a sprite's tint), the pattern's second colour, the outline's
    private const int NoColour = 0, Fill1 = 1, Fill2 = 2, OutlineColour = 3;

    private static int _team = -1;
    private static Emblem _draft = new([]);
    private static int _selected;
    private static bool _picking;
    private static int _colourField;
    private static int _toolPage;
    private static readonly List<Emblem> _undo = [];

    // a drag (a slider's, the preview's, the colour picker's) being one undo step, not one per value
    private static string? _dragKey;

    // the preview's pointer: the layer moved and where it was taken from it, and a selection to redraw on release
    private static bool _moving;
    private static Vector2 _grab;
    private static bool _reselect;

    private static YKLayout? _left;
    private static YKLayout? _right;
    private static bool _leads;
    private static Image? _preview;
    private static readonly List<Image> _small = [];

    private static readonly Color Dark = new(0.2f, 0.17f, 0.13f, 1f);

    /// <summary>
    ///     The Emblem page of the Team tab: the big preview and the emblem's buttons on the left page; the layers and
    ///     the selected layer's tools (or the picture grid, or the colour picker, while open) on the right page.
    ///     Anyone but the leader sees the emblem and its layers only
    /// </summary>
    internal static void Build(YKLayout left, YKLayout right, int teamId, bool leads)
    {
        if (_team != teamId) {
            _team = teamId;
            _draft = Saved(teamId);
            _selected = 0;
            _undo.Clear();
            _picking = false;
            _colourField = NoColour;
        }

        _left = left;
        _right = right;
        _leads = leads;
        if (!leads) {
            _draft = Saved(teamId);
        }

        Fill();
    }

    // the draft differs from what the team has (the Save button and its note)
    private static bool Unsaved => TeamEmblem.Format(_draft) != TeamEmblem.Format(Saved(_team));

    private static Emblem Saved(int teamId) => TeamBanner.EmblemOf(teamId) ?? new([]);

    private static void Clear(YKLayout page)
    {
        foreach (Transform child in page.transform) {
            Object.Destroy(child.gameObject);
        }
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
        if (_draft.Layers.Count == 0) {
            _colourField = NoColour;
        }

        // the left page: the preview, centred, with air above it
        var view = left.Horizontal();
        view.Layout.childAlignment = TextAnchor.MiddleCenter;
        view.Layout.childForceExpandWidth = false;
        view.Layout.childControlHeight = false;
        view.Layout.padding = new(0, 0, 10, 6);
        view.LayoutElement().minHeight = view.LayoutElement().preferredHeight = PreviewSize + 16f;
        view.LayoutElement().flexibleWidth = 1f;
        _preview = TeamEmblemBaker.Place(view.transform, TeamEmblemBaker.Get(_draft), PreviewSize);
        // (as the emblem shows elsewhere: a player's card and a team's chip, user 2026-10-30)
        var small = view.Vertical();
        small.Layout.childAlignment = TextAnchor.MiddleCenter;
        small.Layout.spacing = 8f;
        small.Layout.padding = new(16, 0, 0, 0);
        _small.Clear();
        foreach (var size in new[] { 48f, 24f }) {
            _small.Add(TeamEmblemBaker.Place(small.transform, TeamEmblemBaker.Get(_draft), size));
        }

        if (!_leads) {
            right.TextSmall("emp_ui_emblem_layers".Loc(_draft.Layers.Count, TeamEmblem.MaxLayers), FontColor.Gray);
            var names = right.ListArea(_draft.Layers.Count, 10, 26f);
            foreach (var layer in _draft.Layers) {
                names.Text(NameOf(layer.Thing));
            }

            right.TextSmall("emp_ui_team_tools_leader_only".lang(), FontColor.Gray);
            return;
        }

        // a click on the preview picks the layer under the pointer, a drag moves it
        _preview.raycastTarget = true;
        var input = _preview.gameObject.AddComponent<EmblemPreviewInput>();
        input.Down = PointerDown;
        input.Drag = PointerDrag;
        input.Up = PointerUp;

        // what is added: a shape or a picture
        var adds = left.ButtonRow();
        adds.Layout.childAlignment = TextAnchor.MiddleCenter;
        var room = _draft.Layers.Count < TeamEmblem.MaxLayers;
        UIButton? shapes = null;
        shapes = Part(adds.RowButton("emp_ui_emblem_add_shape".lang(), () => ShowShapes(shapes)), 2);
        shapes.interactable = room;
        Part(adds.RowButton("emp_ui_emblem_add_picture".lang(), () => {
            _picking = true;
            _colourField = NoColour;
            Fill();
        }), 2).interactable = room;

        // (cut to the bottom layer: how every emblem drew before shapes became layers)
        var clipLine = left.Row();
        var clip = clipLine.Toggle("emp_ui_emblem_clip".lang(), _draft.Clip, on => {
            Change(_draft with { Clip = on });
            Refresh();
        });
        clip.LayoutElement().flexibleWidth = 1f;
        clip.AsRow();

        // (three buttons in the page's width: each a third of it, widened only by a longer label)
        var bar = left.ButtonRow();
        bar.Layout.childAlignment = TextAnchor.MiddleCenter;
        Part(bar.RowButton("emp_ui_emblem_undo".lang(), () => {
            if (_undo.Count == 0) {
                return;
            }

            _draft = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Fill();
        }), 3).interactable = _undo.Count > 0;
        Part(bar.RowButton("emp_ui_emblem_reset".lang(), () => {
            Change(Saved(_team));
            Fill();
        }), 3);
        Part(bar.RowButton("emp_ui_emblem_save".lang(), () => {
            // (the team's colour: the bottom shape's when it is one of the twelve team colours; else it stays)
            var colour = TeamEmblem.TeamColour(_draft) ?? TeamBanner.Of(_team)?.Color ?? 0;
            Teams.Ask(TeamAsk.Banner, 0, colour, TeamEmblem.Format(_draft));
        }), 3).interactable = _draft.Layers.Count > 0 && Unsaved;
        if (Unsaved && _draft.Layers.Count > 0) {
            left.TextSmall("emp_ui_emblem_unsaved".lang(), FontColor.Warning);
        }

        var more = left.ButtonRow();
        more.Layout.childAlignment = TextAnchor.MiddleCenter;
        Part(more.RowButton("emp_ui_emblem_random".lang(), () => {
            Change(RandomEmblem());
            _selected = 0;
            _colourField = NoColour;
            Fill();
        }), 2);
        UIButton? starters = null;
        starters = Part(more.RowButton("emp_ui_emblem_templates".lang(), () => ShowStarters(starters)), 2);

        // the right page: the picture grid, the colour picker, or the layers and the selected layer's tools
        if (_picking) {
            right.ButtonRow().RowButton("emp_ui_emblem_add_close".lang(), () => {
                _picking = false;
                Fill();
            });
            TeamBanner.BuildThingPicker(right, Dark, thing => {
                if (_draft.Layers.Count >= TeamEmblem.MaxLayers) {
                    return;
                }

                Change(_draft with { Layers = [.._draft.Layers, TeamEmblem.Layer(thing)] });
                _selected = _draft.Layers.Count - 1;
                _picking = false;
                Fill();
            });
        } else if (_colourField != NoColour && _draft.Layers.Count > 0) {
            BuildColour(right);
        } else {
            right.TextSmall("emp_ui_emblem_layers".Loc(_draft.Layers.Count, TeamEmblem.MaxLayers), FontColor.Gray);
            var layers = right.ListArea(_draft.Layers.Count, 3, UIHelper.RowHeight);
            // (the top layer first, as a stack is seen)
            for (var i = _draft.Layers.Count - 1; i >= 0; i--) {
                LayerRow(layers, i);
            }

            if (_draft.Layers.Count > 0) {
                TeamBanner.Pills(right, ["emp_ui_emblem_tools_place".lang(), "emp_ui_emblem_tools_look".lang()], _toolPage, i => {
                    _toolPage = i;
                    Fill();
                });
                if (_toolPage == 0) {
                    PlaceTools(right);
                } else {
                    LookTools(right);
                }
            } else {
                // (an empty list said nothing: how to start, user 2026-10-30 "on peut faire beaucoup mieux")
                right.EmptyText("emp_ui_emblem_start".lang());
            }
        }

        if (left.transform.parent is RectTransform spread) {
            LayoutRebuilder.MarkLayoutForRebuild(spread);
        }
    }

    /// <summary>A button a share of the page's width (widened only by a longer label)</summary>
    private static UIButton Part(UIButton button, int of)
    {
        button.LayoutElement().preferredWidth = (EmpUi.PageInner - 10f * (of - 1)) / of;
        return button;
    }

    /// <summary>The layer alone, centred and full size (its thumbnail in the list)</summary>
    private static Sprite Thumb(EmblemLayer layer) =>
        TeamEmblemBaker.Get(new([layer with { X = TeamEmblem.Grid / 2, Y = TeamEmblem.Grid / 2, Size = 100, Rotation = 0, Flip = 0, Opacity = 100 }]));

    /// <summary>A layer: its thumbnail and name (a click selects it), then up, down and remove</summary>
    private static void LayerRow(YKLayout parent, int index)
    {
        var layer = _draft.Layers[index];
        var row = parent.Row();
        TeamBanner.Swatch(row, Dark, 26f, 26f, index == _selected, () => {
            _selected = index;
            Fill();
        });
        var thumb = TeamEmblemBaker.Place(row.transform.GetChild(row.transform.childCount - 1), Thumb(layer), 24f);
        var trt = thumb.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new(0.5f, 0.5f);
        trt.anchoredPosition = Vector2.zero;
        var name = NameOf(layer.Thing);
        row.Text(index == _selected ? "> " + name : name).LayoutElement().flexibleWidth = 1f;
        // the game's own small list buttons (its playlist's up and down, the inventory's trash); up draws it higher
        var icons = EmpUi.GameIcons;
        row.IconButton(icons?.up, "^", () => Move(index, 1), "emp_ui_emblem_up", index < _draft.Layers.Count - 1, size: 26f);
        row.IconButton(icons?.down, "v", () => Move(index, -1), "emp_ui_emblem_down", index > 0, size: 26f);
        row.IconButton(icons?.trash, "x", () => {
            Change(_draft with { Layers = _draft.Layers.Where((_, i) => i != index).ToList() });
            if (_selected >= index && _selected > 0) {
                _selected--;
            }

            Fill();
        }, "emp_ui_emblem_remove", size: 26f);
    }

    /// <summary>The move pad: the four nudges as a 3x3 cross of small arrows</summary>
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
            line!.IconButton(icons?.up, cell.Sign, () => Edit(l => l with {
                X = Mathf.Clamp(l.X + dx * Step, TeamEmblem.MinPos, TeamEmblem.MaxPos),
                Y = Mathf.Clamp(l.Y + dy * Step, TeamEmblem.MinPos, TeamEmblem.MaxPos),
            }), null, true, cell.Angle, 28f);
        }
    }

    /// <summary>The selected layer's place: nudges, turn, flips, size, angle, opacity, duplicate, mirror</summary>
    private static void PlaceTools(YKLayout root)
    {
        var layer = _draft.Layers[_selected];

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

        Stepper(root, "emp_ui_emblem_size", layer.Size, TeamEmblem.MinSize, TeamEmblem.MaxSize, 10,
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
    ///     The selected layer's look: its colour (a picture's tint, or its own colours), a shape's pattern and second
    ///     colour, points, height and thickness, then the outline's width and colour
    /// </summary>
    private static void LookTools(YKLayout root)
    {
        var layer = _draft.Layers[_selected];
        var colours = root.Row();
        ColourButton(colours, layer.IsShape ? "emp_ui_emblem_colour1" : "emp_ui_emblem_tint", layer.Color, Fill1, true);
        if (!layer.IsShape) {
            var none = colours.Button("emp_ui_emblem_tint_none".lang(), () => {
                Edit(l => l with { Color = TeamEmblem.Own });
                Fill();
            });
            none.LayoutElement().preferredWidth = 90f;
            none.LayoutElement().flexibleWidth = 0f;
            none.interactable = layer.Color != TeamEmblem.Own;
        } else {
            var pattern = root.Row();
            Choice(pattern, "emp_ui_emblem_pattern",
                Enumerable.Range(0, TeamEmblem.PatternCount).Select(i => $"emp_ui_emblem_pattern_{i}").ToArray(),
                layer.Pattern, i => {
                    Edit(l => l with { Pattern = i });
                    Fill();
                });
            ColourButton(pattern, "emp_ui_emblem_colour2", layer.Color2, Fill2, layer.Pattern != 0);

            var kind = layer.Kind;
            if (kind is "star" or "polygon") {
                Stepper(root, kind == "star" ? "emp_ui_emblem_points" : "emp_ui_emblem_sides", layer.Sides, 3, 12, 1,
                    (l, v) => l with { Sides = v }, l => l.Sides, "");
            }

            if (kind != "line") {
                Stepper(root, "emp_ui_emblem_height", layer.Aspect, 10, 100, 10, (l, v) => l with { Aspect = v }, l => l.Aspect, " %");
            }

            if (kind is "rounded" or "star" or "line" or "cross" or "ring") {
                Stepper(root, "emp_ui_emblem_thick", layer.Thick, 5, 90, 5, (l, v) => l with { Thick = v }, l => l.Thick, " %");
            }
        }

        Stepper(root, "emp_ui_emblem_outline", layer.Outline, 0, TeamEmblem.MaxOutline, 1,
            (l, v) => l with { Outline = v }, l => l.Outline, " px");
        ColourButton(root.Row(), "emp_ui_emblem_outline_colour", layer.OutlineColor, OutlineColour, true);
    }

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
        button.interactable = enabled;
    }

    /// <summary>The colour picker for the field being picked, with the layer's name and Done over it</summary>
    private static void BuildColour(YKLayout root)
    {
        var layer = _draft.Layers[_selected];
        var head = root.Row();
        var title = _colourField switch {
            Fill2 => "emp_ui_emblem_colour2",
            OutlineColour => "emp_ui_emblem_outline_colour",
            _ => layer.IsShape ? "emp_ui_emblem_colour1" : "emp_ui_emblem_tint",
        };
        head.Text(NameOf(layer.Thing) + ": " + title.lang()).LayoutElement().flexibleWidth = 1f;
        var done = head.Button("emp_ui_colour_done".lang(), () => {
            _colourField = NoColour;
            Fill();
        });
        done.LayoutElement().preferredWidth = 100f;
        done.LayoutElement().flexibleWidth = 0f;

        var start = _colourField switch {
            Fill2 => layer.Color2,
            OutlineColour => layer.OutlineColor,
            _ => layer.Color == TeamEmblem.Own && !layer.IsShape ? 0xFFFFFFFFu : layer.Color,
        };
        var field = _colourField;
        ColourPicker.Build(root, start, TeamColours(), rgba => Edit(l => field switch {
            Fill2 => l with { Color2 = rgba },
            OutlineColour => l with { OutlineColor = rgba },
            // (a picture's tint keeps its alpha apart from "own colours": never the 0 that means them)
            _ => l with { Color = !l.IsShape && rgba == TeamEmblem.Own ? 0x00000001u : rgba },
        }, "colour"), _ => _dragKey = null);
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
        }

        return list.Distinct().Take(12).ToList();
    }

    /// <summary>A title in grey over one of the game's dropdowns, half the row wide</summary>
    private static void Choice(YKLayout row, string titleId, string[] optionIds, int value, System.Action<int> change)
    {
        var col = row.Vertical();
        col.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        col.Layout.spacing = 0f;
        col.LayoutElement().flexibleWidth = 1f;
        col.LayoutElement().preferredWidth = 0f;
        col.TextSmall(titleId.lang(), FontColor.Gray);
        var dropdown = col.Dropdown(optionIds.Select(id => id.lang()).ToList(), change, value);
        dropdown.LayoutElement().flexibleWidth = 1f;
        dropdown.LayoutElement().preferredHeight = UIHelper.RowHeight;
        dropdown.Rect().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UIHelper.RowHeight);
    }

    /// <summary>A number of the selected layer: its title, "-", the game's slider, "+"</summary>
    private static void Stepper(YKLayout root, string titleId, int value, int min, int max, int step,
        System.Func<EmblemLayer, int, EmblemLayer> set, System.Func<EmblemLayer, int> get, string unit)
    {
        var row = root.Row();
        row.Text(titleId.lang());
        Slider? slider = null;
        row.SignButton("-", () => {
            Edit(l => set(l, Mathf.Clamp(get(l) - step, min, max)));
            slider?.SetValueWithoutNotify(get(_draft.Layers[_selected]));
        });
        slider = row.Slider(value, v => {
            var rounded = Mathf.RoundToInt(v);
            if (rounded == get(_draft.Layers[_selected])) {
                return;
            }

            // (one undo step for the whole drag)
            Edit(l => set(l, rounded), titleId);
        }, min, max, v => Mathf.RoundToInt(v) + unit);
        slider.LayoutElement().flexibleWidth = 1f;
        row.SignButton("+", () => {
            Edit(l => set(l, Mathf.Clamp(get(l) + step, min, max)));
            slider.SetValueWithoutNotify(get(_draft.Layers[_selected]));
        });
    }

    /// <summary>
    ///     A copy of the selected layer on the other side of the emblem, turned the other way (a pair of wings, two
    ///     crossed swords): the two halves of a symmetric emblem in one click (user 2026-10-30)
    /// </summary>
    private static void Mirror()
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers || _selected >= _draft.Layers.Count) {
            return;
        }

        var layer = _draft.Layers[_selected];
        var copy = layer with { X = TeamEmblem.Grid - layer.X, Flip = layer.Flip ^ 1, Rotation = (360 - layer.Rotation) % 360 };
        var list = _draft.Layers.ToList();
        list.Insert(_selected + 1, copy);
        Change(_draft with { Layers = list });
        _selected++;
        Fill();
    }

    private static void Duplicate()
    {
        if (_draft.Layers.Count >= TeamEmblem.MaxLayers || _selected >= _draft.Layers.Count) {
            return;
        }

        // the copy just above the original, a little aside so it shows
        var copy = _draft.Layers[_selected];
        copy = copy with {
            X = Mathf.Clamp(copy.X + 4, TeamEmblem.MinPos, TeamEmblem.MaxPos), Y = Mathf.Clamp(copy.Y + 4, TeamEmblem.MinPos, TeamEmblem.MaxPos),
        };
        var list = _draft.Layers.ToList();
        list.Insert(_selected + 1, copy);
        Change(_draft with { Layers = list });
        _selected++;
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
        Change(_draft with { Layers = [.._draft.Layers, TeamEmblem.Shape(kind, colour, first ? 90 : 50)] });
        _selected = _draft.Layers.Count - 1;
        _picking = false;
        _colourField = NoColour;
        Fill();
    }

    // ---------------------------------------------------------------- the preview's pointer

    /// <summary>The topmost layer whose square holds this point of the grid, or -1</summary>
    private static int LayerAt(Vector2 p)
    {
        for (var i = _draft.Layers.Count - 1; i >= 0; i--) {
            if (Holds(_draft.Layers[i], p)) {
                return i;
            }
        }

        return -1;
    }

    private static bool Holds(EmblemLayer l, Vector2 p)
    {
        var half = TeamEmblem.Grid * l.Size / 200f;
        return Mathf.Abs(p.x - l.X) <= half && Mathf.Abs(p.y - l.Y) <= half;
    }

    private static void PointerDown(Vector2 p)
    {
        // (the selected layer keeps the pointer while it is under it, even under another one)
        var keep = _selected < _draft.Layers.Count && Holds(_draft.Layers[_selected], p);
        var hit = keep ? _selected : LayerAt(p);
        _moving = hit >= 0;
        if (!_moving) {
            return;
        }

        if (hit != _selected) {
            _selected = hit;
            _reselect = true;
        }

        var layer = _draft.Layers[hit];
        _grab = p - new Vector2(layer.X, layer.Y);
    }

    private static void PointerDrag(Vector2 p)
    {
        if (!_moving) {
            return;
        }

        var x = Mathf.Clamp(Mathf.RoundToInt(p.x - _grab.x), TeamEmblem.MinPos, TeamEmblem.MaxPos);
        var y = Mathf.Clamp(Mathf.RoundToInt(p.y - _grab.y), TeamEmblem.MinPos, TeamEmblem.MaxPos);
        if (_draft.Layers[_selected] is var l && l.X == x && l.Y == y) {
            return;
        }

        Edit(layer => layer with { X = x, Y = y }, "preview");
    }

    private static void PointerUp()
    {
        _moving = false;
        _dragKey = null;
        if (_reselect) {
            _reselect = false;
            // (the layer list and the tools show the layer picked on the preview)
            Fill();
        }
    }

    // ---------------------------------------------------------------- random and starters

    /// <summary>
    ///     A random emblem: a shape of two colours, one or two things of the game over it (some turned, tinted or
    ///     outlined, the first sometimes reaching past the shape), sometimes a ring or a star
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
        var layers = new List<EmblemLayer> { ground };
        if (rng.Next(3) == 0) {
            layers.Add(TeamEmblem.Shape(rng.Next(2) == 0 ? "ring" : "star", TeamEmblem.Rgba(rng.Next(TeamBanner.EmblemColorCount)), 60 + rng.Next(30)));
        }

        var rows = EClass.sources.things.rows;
        for (var n = layers.Count + 1 + rng.Next(2); layers.Count < n;) {
            string? thing = null;
            for (var tries = 0; tries < 30 && thing == null; tries++) {
                var row = rows[rng.Next(rows.Count)];
                if (!string.IsNullOrEmpty(row.id) && TeamBanner.Normalize("t:" + row.id) is { } typed && TeamBanner.SpriteOf(typed) != null) {
                    thing = typed;
                }
            }

            if (thing == null) {
                break;
            }

            var first = layers.Count(l => !l.IsShape) == 0;
            layers.Add(new(thing, first ? 32 : 20 + rng.Next(24), first ? 32 : 20 + rng.Next(24),
                first ? 70 + rng.Next(60) : 40 + rng.Next(30), rng.Next(3) == 0 ? rng.Next(8) * 45 : 0, rng.Next(4) == 0 ? 1 : 0,
                rng.Next(2) == 0 ? TeamEmblem.Own : TeamEmblem.Rgba(rng.Next(TeamBanner.EmblemColorCount)), 100, rng.Next(2)));
        }

        return new(layers, rng.Next(3) == 0);
    }

    private static Emblem Old(int bg, int shape, int pattern, int bg2, params TeamEmblem.OldLayer[] layers) =>
        TeamEmblem.FromLegacy(new(bg, shape, layers, pattern, bg2))!;

    /// <summary>A few ready emblems of the game's own things and monsters (one the game does not have is left out)</summary>
    private static readonly Emblem[] Starters = [
        Old(19, 1, 0, 19, new TeamEmblem.OldLayer("c:putty", 32, 34, 90, 0, 0, TeamEmblem.OwnColours, 100, 1)),
        Old(13, 2, 1, 16, new TeamEmblem.OldLayer("t:mathammer", 32, 30, 110, 1, 0, 18, 100, 1)),
        Old(12, 3, 5, 0, new TeamEmblem.OldLayer("t:torch", 32, 28, 100, 0, 0, TeamEmblem.OwnColours, 100, 1)),
        Old(7, 4, 7, 18, new TeamEmblem.OldLayer("t:crystal_mana", 32, 32, 80, 0, 0, TeamEmblem.OwnColours, 100, 0)),
        Old(14, 5, 6, 6, new TeamEmblem.OldLayer("c:hound", 32, 34, 90, 0, 0, TeamEmblem.OwnColours, 100, 1)),
        Old(16, 0, 3, 17, new TeamEmblem.OldLayer("t:crystal_sun", 22, 22, 60, 0, 0, 18, 100, 0), new TeamEmblem.OldLayer("t:crystal_earth", 42, 42, 60, 0, 0, 2, 100, 0)),
        // (shapes as layers: a sword past a round ground, a star in a ring)
        new([
            TeamEmblem.Shape("round", TeamEmblem.Rgba(12), 76) with { Outline = 2, OutlineColor = TeamEmblem.Rgba(18) },
            new("t:sword", 32, 32, 130, 315, 0, TeamEmblem.Own, 100, 1),
        ]),
        new([
            TeamEmblem.Shape("ring", TeamEmblem.Rgba(18), 96),
            TeamEmblem.Shape("star", TeamEmblem.Rgba(13), 70) with { Outline = 1 },
        ]),
    ];

    /// <summary>The starters in the game's context menu under the button, by the name of their main thing</summary>
    private static void ShowStarters(UIButton? anchor)
    {
        var menu = EClass.ui.CreateContextMenuInteraction();
        if (anchor != null) {
            menu.SetHighlightTarget(anchor);
        }

        foreach (var starter in Starters) {
            var layers = starter.Layers.Where(l => l.IsShape || TeamBanner.Normalize(l.Thing) != null).ToList();
            if (!layers.Any(l => !l.IsShape) && starter.Layers.Any(l => !l.IsShape)) {
                continue;
            }

            var pick = starter with { Layers = layers };
            var main = layers.FirstOrDefault(l => !l.IsShape).Thing ?? layers[layers.Count - 1].Thing;
            menu.AddButton(NameOf(main), () => {
                Change(pick);
                _selected = 0;
                _colourField = NoColour;
                Fill();
            });
        }

        menu.Show();
    }

    // ---------------------------------------------------------------- the draft

    /// <summary>
    ///     The selected layer changed: the draft and the preview only (the buttons stay as they are). A
    ///     <paramref name="drag" /> key makes its repeated changes one undo step
    /// </summary>
    private static void Edit(System.Func<EmblemLayer, EmblemLayer> change, string? drag = null)
    {
        var undoStep = drag == null || drag != _dragKey;
        _dragKey = drag;
        if (_selected >= _draft.Layers.Count) {
            return;
        }

        var list = _draft.Layers.ToList();
        list[_selected] = change(list[_selected]);
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
        }

        _draft = next;
    }

    private static void Refresh()
    {
        var sprite = TeamEmblemBaker.Get(_draft);
        if (_preview != null) {
            _preview.sprite = sprite;
        }

        foreach (var image in _small) {
            if (image != null) {
                image.sprite = sprite;
            }
        }
    }

    private static string NameOf(string thing)
    {
        var id = thing.Length > 2 ? thing.Substring(2) : thing;
        if (thing.StartsWith("g:")) {
            return ("emp_ui_emblem_" + id).lang();
        }

        if (thing.StartsWith("c:")) {
            return EClass.sources.charas.map.TryGetValue(id, out var chara) ? chara.GetName() ?? id : id;
        }

        return EClass.sources.things.map.TryGetValue(id, out var row) ? row.GetName() ?? id : id;
    }

    // ---------------------------------------------------------------- the bench (Debug eval)

    /// <summary>The draft's canonical text (the bench reads it)</summary>
    internal static string DraftText => TeamEmblem.Format(_draft);

    /// <summary>The bench sets the draft from a text, as Starters would</summary>
    internal static bool SetDraft(string text)
    {
        if (TeamEmblem.Validate(TeamEmblem.Parse(text), TeamBanner.Normalize) is not { } emblem) {
            return false;
        }

        Change(emblem);
        _selected = emblem.Layers.Count - 1;
        _colourField = NoColour;
        Fill();
        return true;
    }
}

/// <summary>
///     The emblem preview's pointer, in points of the 64 grid from the top left: pressed, dragged, released
/// </summary>
internal sealed class EmblemPreviewInput : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IPointerUpHandler
{
    internal System.Action<Vector2>? Down;
    internal System.Action<Vector2>? Drag;
    internal System.Action? Up;

    private Vector2 OnGrid(PointerEventData data)
    {
        var rt = (RectTransform)transform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, data.position, data.pressEventCamera, out var p);
        var r = rt.rect;
        return new((p.x - r.xMin) / Mathf.Max(1f, r.width) * TeamEmblem.Grid, (r.yMax - p.y) / Mathf.Max(1f, r.height) * TeamEmblem.Grid);
    }

    public void OnPointerDown(PointerEventData data) => Down?.Invoke(OnGrid(data));

    public void OnBeginDrag(PointerEventData data)
    {
    }

    public void OnDrag(PointerEventData data) => Drag?.Invoke(OnGrid(data));

    public void OnPointerUp(PointerEventData data) => Up?.Invoke();
}
