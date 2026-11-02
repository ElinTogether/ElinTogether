using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     My drawings (lead dev 2026-11-02): the player's own drawings and those kept from other players, on every server
///     they play (<see cref="DrawingStore" />). The left page lists them (a thumbnail, the title, "by" its creator) under
///     the new drawing's size and button; the right page shows the picked one large with its credit, and Open, Rename,
///     Duplicate, Delete. An opened drawing fills both pages with the emblem editor (<see cref="EmblemEditor" />) until
///     it is closed
/// </summary>
internal static class DrawingsPage
{
    private const float Thumb = 40f;
    private const float Large = 200f;

    private static YKLayout? _left;
    private static YKLayout? _right;
    private static string? _open;
    private static string? _picked;
    private static bool _renaming;
    private static bool _deleting;
    private static int _w = 3, _h = 3;

    // the thumbnails, by the drawing's text (freed when the list is built again without them)
    private static readonly Dictionary<string, Sprite> _sprites = [];

    internal static void Build(YKLayout left, YKLayout right)
    {
        (_left, _right) = (left, right);
        Fill();
    }

    private static void Clear(YKLayout page)
    {
        foreach (Transform child in page.transform) {
            child.gameObject.SetActive(false);
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
        if (_open != null && DrawingStore.Get(_open) is { } doc) {
            EmblemEditor.BuildDrawing(_left, _right, _open, doc, () => {
                _picked = _open;
                _open = null;
                Fill();
            }, forked => {
                // (another player's drawing saved here became a new one: that one is open now)
                _open = forked;
                _picked = forked;
            });
            return;
        }

        _open = null;
        BuildList(_left);
        BuildPicked(_right);
        Laid();
    }

    private static void Laid()
    {
        if (_left?.GetComponentInParent<TabEmpBase>() is { } tab) {
            tab.Relayout();
        }
    }

    /// <summary>A drawing's thumbnail, baked once per text</summary>
    private static Sprite SpriteOf(Drawing d)
    {
        var key = d.Format();
        if (_sprites.TryGetValue(key, out var s) && s != null) {
            return s;
        }

        var tex = Drawing.Bake(d);
        return _sprites[key] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    /// <summary>The thumbnails of drawings no longer listed destroyed</summary>
    private static void Prune()
    {
        var keep = new HashSet<string>(DrawingStore.All.Select(e => e.Drawing.Format()));
        foreach (var key in _sprites.Keys.Where(k => !keep.Contains(k)).ToList()) {
            if (_sprites[key] is { } s) {
                TeamEmblemBaker.Free(s);
            }

            _sprites.Remove(key);
        }
    }

    /// <summary>The picture of a drawing at most <paramref name="box" /> on its longer side, pixels kept sharp</summary>
    private static Image Picture(Transform parent, Drawing d, float box)
    {
        var holder = new GameObject("Drawing", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        holder.transform.SetParent(parent, false);
        ((RectTransform)holder.transform).sizeDelta = new(box, box);
        var element = holder.GetComponent<LayoutElement>();
        (element.minWidth, element.preferredWidth, element.minHeight, element.preferredHeight, element.flexibleWidth) = (box, box, box, box, 0f);
        var ground = holder.GetComponent<Image>();
        ground.color = new(0.28f, 0.22f, 0.13f, 0.10f);
        ground.raycastTarget = false;
        var pic = new GameObject("Picture", typeof(RectTransform), typeof(Image));
        pic.transform.SetParent(holder.transform, false);
        var rt = (RectTransform)pic.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new(0.5f, 0.5f);
        var scale = (box - 4f) / Mathf.Max(d.Art.W, d.Art.H);
        if (scale >= 1f) {
            scale = Mathf.Floor(scale);
        }

        rt.sizeDelta = new(d.Art.W * scale, d.Art.H * scale);
        var image = pic.GetComponent<Image>();
        image.sprite = SpriteOf(d);
        image.raycastTarget = false;
        return image;
    }

    private static void BuildList(YKLayout left)
    {
        Prune();
        var all = DrawingStore.All;
        left.TextSmall("emp_ui_drawing_list".Loc(all.Count, DrawingStore.MaxDrawings), FontColor.Gray);

        // a new drawing: its width and height (16 to 256 pixels), then the button
        var sizes = Drawing.Sides.Select(s => s.ToString()).ToList();
        var row = left.Row();
        row.Layout.spacing = 6f;
        var wd = row.Dropdown(sizes, i => _w = i, Mathf.Clamp(_w, 0, sizes.Count - 1));
        wd.LayoutElement().preferredWidth = 74f;
        wd.LayoutElement().preferredHeight = UIHelper.RowHeight;
        row.Text("x").LayoutElement().minWidth = 12f;
        var hd = row.Dropdown(sizes, i => _h = i, Mathf.Clamp(_h, 0, sizes.Count - 1));
        hd.LayoutElement().preferredWidth = 74f;
        hd.LayoutElement().preferredHeight = UIHelper.RowHeight;
        var make = row.Button("emp_ui_drawing_new".lang(), () => {
            var d = Drawing.New(Drawing.Sides[_w], Drawing.Sides[_h], "");
            if (DrawingStore.Save(d) is { } id) {
                _open = id;
                _picked = id;
                Fill();
            }
        });
        make.LayoutElement().flexibleWidth = 1f;
        make.LayoutElement().preferredWidth = 0f;
        make.interactable = all.Count < DrawingStore.MaxDrawings;

        if (all.Count == 0) {
            left.EmptyText("emp_ui_drawing_none".lang());
            return;
        }

        var list = left.ScrollList(4 * (Thumb + 8f), true);
        list.Layout.spacing = 2f;
        foreach (var (id, d) in all) {
            var line = list.Horizontal();
            line.Layout.childForceExpandWidth = false;
            line.Layout.childAlignment = TextAnchor.MiddleLeft;
            line.Layout.spacing = 8f;
            line.Layout.padding = new(4, 4, 2, 2);
            line.LayoutElement().minHeight = line.LayoutElement().preferredHeight = Thumb + 6f;
            var pick = id;
            EmpUi.Pickable(line, id == _picked, () => {
                _picked = pick;
                _renaming = _deleting = false;
                Fill();
            });
            Picture(line.transform, d, Thumb);
            var words = line.Vertical();
            words.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            words.Layout.spacing = 0f;
            words.LayoutElement().flexibleWidth = 1f;
            words.LayoutElement().preferredWidth = 0f;
            words.Text(d.Title.Length > 0 ? d.Title : "emp_ui_drawing_untitled".lang());
            words.TextSmall("emp_ui_drawing_by".Loc(d.CreatorName) + "  ·  " + d.Art.W + " x " + d.Art.H, FontColor.Gray);
        }
    }

    private static void BuildPicked(YKLayout right)
    {
        if (_picked == null || DrawingStore.Get(_picked) is not { } d) {
            right.EmptyText("emp_ui_drawing_pick".lang());
            return;
        }

        var id = _picked;
        var view = right.Horizontal();
        view.Layout.childAlignment = TextAnchor.MiddleCenter;
        view.Layout.childForceExpandWidth = false;
        view.LayoutElement().minHeight = view.LayoutElement().preferredHeight = Large + 8f;
        view.LayoutElement().flexibleWidth = 1f;
        Picture(view.transform, d, Large);

        right.Text(d.Title.Length > 0 ? d.Title : "emp_ui_drawing_untitled".lang());
        // (the credit, always: lead dev 2026-11-02, a drawing stays its creator's)
        right.TextSmall("emp_ui_drawing_by".Loc(d.CreatorName), FontColor.Gray);
        if (d.BasedOnName.Length > 0) {
            right.TextSmall("emp_ui_drawing_based_on".Loc(d.BasedOnName), FontColor.Gray);
        }

        var when = System.DateTimeOffset.FromUnixTimeSeconds(d.Created).ToLocalTime();
        right.TextSmall(d.Art.W + " x " + d.Art.H + "  ·  " + when.ToString("yyyy-MM-dd HH:mm"), FontColor.Gray);

        if (_renaming) {
            var line = right.Row();
            var field = line.InputText(d.Title);
            field.field.characterLimit = Drawing.MaxTitle;
            field.LayoutElement().flexibleWidth = 1f;
            var ok = line.Button("emp_ui_drawing_rename_ok".lang(), () => {
                DrawingStore.Rename(id, field.Text);
                _renaming = false;
                Fill();
            });
            ok.LayoutElement().preferredWidth = 90f;
            ok.LayoutElement().flexibleWidth = 0f;
        }

        var actions = right.ButtonRow();
        Even(actions.RowButton("emp_ui_drawing_open".lang(), () => {
            _open = id;
            _renaming = _deleting = false;
            Fill();
        }));
        Even(actions.RowButton("emp_ui_drawing_rename".lang(), () => {
            _renaming = !_renaming;
            _deleting = false;
            Fill();
        }));
        Even(actions.RowButton("emp_ui_drawing_duplicate".lang(), () => {
            if (DrawingStore.Duplicate(id) is { } copy) {
                _picked = copy;
            }

            Fill();
        })).interactable = DrawingStore.All.Count < DrawingStore.MaxDrawings;
        // (a second click deletes: nothing is lost by a slip)
        Even(actions.RowButton((_deleting ? "emp_ui_drawing_delete_sure" : "emp_ui_drawing_delete").lang(), () => {
            if (!_deleting) {
                _deleting = true;
                Fill();
                return;
            }

            DrawingStore.Delete(id);
            EmblemEditor.Forget(id);
            _deleting = false;
            _picked = null;
            Fill();
        }));
    }

    private static UIButton Even(UIButton button)
    {
        var element = button.LayoutElement();
        element.flexibleWidth = 1f;
        element.preferredWidth = 0f;
        element.minWidth = 40f;
        return button;
    }

    // ---------------------------------------------------------------- the bench (Debug eval)

    /// <summary>The drawings' texts, one per line (the bench reads them)</summary>
    internal static string ListText => string.Join("\n", DrawingStore.All.Select(e => e.Id + "\t" + e.Drawing.Format()));

    /// <summary>The bench opens a drawing as the Open button does</summary>
    internal static void OpenForBench(string id)
    {
        _open = id;
        _picked = id;
        Fill();
    }
}
