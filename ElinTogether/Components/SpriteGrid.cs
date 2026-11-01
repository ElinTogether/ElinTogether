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
///     Every picture of one source (things, monsters, the game's icons, gods, UI icons, tiles, portraits, places:
///     <see cref="EmblemSprites" />) in one scrolled grid (lead dev 2026-11-01: no pages), filtered as the
///     player types (the name in the player's language, the English name or the id). Only the rows in view have cells:
///     a pool of them is moved and given its sprites as the grid scrolls, so thousands of entries stay quick. The name
///     of the entry under the pointer shows on the line under the grid
/// </summary>
internal static class SpriteGrid
{
    private const float Cell = 38f;
    private const int PerRow = 8;
    private const int Rows = 6;

    /// <summary>
    ///     An entry: its typed id, name, search text, and its sprite (things and monsters, read at once) or how to read
    ///     it when it scrolls into view (the other sources: portraits and pictures are files)
    /// </summary>
    internal sealed record Entry(string Value, string Name, string Search, Sprite? Sprite, System.Func<Sprite?>? Get = null)
    {
        internal Sprite? Picture()
        {
            if (Sprite != null || Get == null) {
                return Sprite;
            }

            try {
                return Get();
            } catch (System.Exception e) {
                EmpLog.Debug(e, "Emblem grid: the picture of {Id} could not be read", Value);
                return null;
            }
        }
    }

    // a pill's entries, read once per language
    private static readonly Dictionary<int, List<Entry>> _lists = [];
    private static string _lang = "";

    /// <summary>The entries of one source pill (<see cref="EmblemSprites.Pills" />), read once per language</summary>
    internal static List<Entry> All(int pill)
    {
        var lang = Lang.langCode ?? "";
        if (_lang != lang) {
            _lists.Clear();
            _lang = lang;
        }

        pill = Mathf.Clamp(pill, 0, EmblemSprites.Pills.Length - 1);
        if (_lists.TryGetValue(pill, out var known)) {
            return known;
        }

        var list = new List<Entry>();
        foreach (var code in EmblemSprites.Pills[pill].Codes) {
            try {
                list.AddRange(code switch {
                    't' => Read(EClass.sources.things.rows.Cast<CardRow>(), "t:"),
                    'c' => Read(EClass.sources.charas.rows.Cast<CardRow>(), "c:"),
                    _ => EmblemSprites.List(code).ToList(),
                });
            } catch (System.Exception e) {
                EmpLog.Debug(e, "Emblem grid: the source {Code} could not be listed", code);
            }
        }

        return _lists[pill] = list.OrderBy(e => e.Name).ToList();
    }

    /// <summary>The entries of one kind (things, or monsters)</summary>
    internal static List<Entry> All(bool monsters) => All(monsters ? 1 : 0);

    private static List<Entry> Read(IEnumerable<CardRow> rows, string type)
    {
        var list = new List<Entry>();
        foreach (var r in rows) {
            if (string.IsNullOrEmpty(r.id)) {
                continue;
            }

            Sprite? sprite;
            try {
                sprite = r.GetSprite();
            } catch {
                sprite = null;
            }

            if (sprite == null) {
                continue;
            }

            var name = r.GetName() is { Length: > 0 } n ? n : r.id;
            list.Add(new(type + r.id, name, (r.id + "\n" + name + "\n" + (r.name ?? "")).ToLowerInvariant(), sprite));
        }

        return list.OrderBy(e => e.Name).ToList();
    }

    /// <summary>The entries whose id or names hold every word of <paramref name="query" /></summary>
    internal static List<Entry> Filter(List<Entry> all, string query)
    {
        var words = query.Trim().ToLowerInvariant().Split([' '], System.StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? all : all.Where(e => words.All(w => e.Search.Contains(w))).ToList();
    }

    /// <summary>
    ///     The grid in a frame of six rows, its hover line under it; a click gives the entry's typed id to
    ///     <paramref name="onPick" />. Returns the view, to give it a new list as the filter changes
    /// </summary>
    internal static SpriteGridView Build(YKLayout parent, Color colour, System.Action<string> onPick)
    {
        var frame = new GameObject("SpriteGrid", typeof(RectTransform), typeof(LayoutElement), typeof(RectMask2D), typeof(Image), typeof(ScrollRect));
        frame.transform.SetParent(parent.transform, false);
        var element = frame.GetComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = Rows * Cell;
        element.minWidth = element.preferredWidth = PerRow * Cell;
        element.flexibleWidth = 0f;
        frame.GetComponent<Image>().color = new(0f, 0f, 0f, 0.08f);

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(frame.transform, false);
        var rt = (RectTransform)content.transform;
        rt.anchorMin = new(0f, 1f);
        rt.anchorMax = new(1f, 1f);
        rt.pivot = new(0.5f, 1f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var scroll = frame.GetComponent<ScrollRect>();
        scroll.content = rt;
        scroll.viewport = (RectTransform)frame.transform;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = Cell;

        var hover = parent.TextSmall("", FontColor.Gray);
        var view = frame.AddComponent<SpriteGridView>();
        view.Init(scroll, rt, colour, onPick, hover);
        return view;
    }

    internal const float CellSize = Cell;
    internal const int Columns = PerRow;
    internal const int VisibleRows = Rows;
}

/// <summary>The grid's cells: as many as the rows in view and one more, moved and refilled as it scrolls</summary>
internal sealed class SpriteGridView : MonoBehaviour
{
    private readonly List<(RectTransform Rt, Image Back, Image Picture, int Index)> _cells = [];
    private List<SpriteGrid.Entry> _list = [];
    private ScrollRect? _scroll;
    private RectTransform? _content;
    private Color _colour;
    private System.Action<string>? _pick;
    private UIText? _hover;
    private int _firstRow = -1;

    internal int Count => _list.Count;

    internal void Init(ScrollRect scroll, RectTransform content, Color colour, System.Action<string> pick, UIText hover)
    {
        (_scroll, _content, _colour, _pick, _hover) = (scroll, content, colour, pick, hover);
        var cells = SpriteGrid.Columns * (SpriteGrid.VisibleRows + 1);
        for (var i = 0; i < cells; i++) {
            var go = new GameObject("Cell", typeof(RectTransform), typeof(Image), typeof(Button), typeof(EventTrigger));
            go.transform.SetParent(content, false);
            var crt = (RectTransform)go.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new(0f, 1f);
            crt.sizeDelta = new(SpriteGrid.CellSize - 4f, SpriteGrid.CellSize - 4f);
            var back = go.GetComponent<Image>();
            back.color = new(colour.r, colour.g, colour.b, 0.35f);
            var pic = new GameObject("Picture", typeof(RectTransform), typeof(Image));
            pic.transform.SetParent(go.transform, false);
            var prt = (RectTransform)pic.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new(0.5f, 0.5f);
            var image = pic.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            var slot = _cells.Count;
            go.GetComponent<Button>().onClick.AddListener(() => Click(slot));
            var trigger = go.GetComponent<EventTrigger>();
            Add(trigger, EventTriggerType.PointerEnter, () => Hover(slot));
            Add(trigger, EventTriggerType.PointerExit, () => Hover(-1));
            _cells.Add((crt, back, image, -1));
        }

        scroll.onValueChanged.AddListener(_ => Refresh());
    }

    private static void Add(EventTrigger trigger, EventTriggerType type, System.Action act)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(_ => act());
        trigger.triggers.Add(entry);
    }

    /// <summary>A new list (the filter changed): back to the top</summary>
    internal void Show(List<SpriteGrid.Entry> list)
    {
        _list = list;
        if (_content == null || _scroll == null) {
            return;
        }

        var rows = (list.Count + SpriteGrid.Columns - 1) / SpriteGrid.Columns;
        _content.sizeDelta = new(0f, rows * SpriteGrid.CellSize);
        _content.anchoredPosition = Vector2.zero;
        _scroll.StopMovement();
        _firstRow = -1;
        Refresh();
        Hover(-1);
    }

    private void Refresh()
    {
        if (_content == null) {
            return;
        }

        var first = Mathf.Max(0, Mathf.FloorToInt(_content.anchoredPosition.y / SpriteGrid.CellSize));
        if (first == _firstRow) {
            return;
        }

        _firstRow = first;
        for (var slot = 0; slot < _cells.Count; slot++) {
            var (rt, back, picture, _) = _cells[slot];
            var index = first * SpriteGrid.Columns + slot;
            if (index >= _list.Count) {
                rt.gameObject.SetActive(false);
                _cells[slot] = (rt, back, picture, -1);
                continue;
            }

            rt.gameObject.SetActive(true);
            var row = index / SpriteGrid.Columns;
            var col = index % SpriteGrid.Columns;
            rt.anchoredPosition = new(col * SpriteGrid.CellSize + 2f, -row * SpriteGrid.CellSize - 2f);
            // (cut to its drawn part, cached: only the entries scrolled into view are read)
            var sprite = TeamBanner.Cropped(_list[index].Picture());
            picture.sprite = sprite;
            // (a picture that does not read: an empty cell, not the white square of an image without sprite)
            picture.enabled = sprite != null;
            if (sprite != null) {
                var inner = SpriteGrid.CellSize - 8f;
                var scale = inner / Mathf.Max(sprite.rect.width, sprite.rect.height, 1f);
                picture.rectTransform.sizeDelta = new(sprite.rect.width * scale, sprite.rect.height * scale);
            }

            _cells[slot] = (rt, back, picture, index);
        }
    }

    private void Click(int slot)
    {
        if (_cells[slot].Index is var i and >= 0 && i < _list.Count) {
            SE.ClickGeneral();
            _pick?.Invoke(_list[i].Value);
        }
    }

    private void Hover(int slot)
    {
        if (_hover == null) {
            return;
        }

        _hover.text = slot >= 0 && _cells[slot].Index is var i and >= 0 && i < _list.Count
            ? _list[i].Name
            : "emp_ui_emblem_grid_count".Loc(_list.Count);
    }
}
