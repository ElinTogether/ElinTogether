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
                EmpLog.Information(e, "Emblem grid: the picture of {Id} could not be read", Value);
                return null;
            }
        }
    }

    // a pill's entries, read once per language (by its source letters, "s:" before them for the stickers' sieved list)
    private static readonly Dictionary<string, List<Entry>> _lists = [];
    private static string _lang = "";

    /// <summary>The entries of one source pill (<see cref="EmblemSprites.Pills" />), read once per language</summary>
    internal static List<Entry> All(int pill) => All(EmblemSprites.Pills[Mathf.Clamp(pill, 0, EmblemSprites.Pills.Length - 1)].Codes);

    /// <summary>
    ///     The entries of those source letters, read once per language. <paramref name="stickers" />: without the
    ///     pictures that make no sticker (blank, tiny, a strip, a flat panel, the same picture twice: <see cref="StickerSieve" />)
    /// </summary>
    internal static List<Entry> All(string codes, bool stickers = false)
    {
        var lang = Lang.langCode ?? "";
        if (_lang != lang) {
            _lists.Clear();
            _lang = lang;
        }

        var key = (stickers ? "s:" : "") + codes;
        if (_lists.TryGetValue(key, out var known)) {
            return known;
        }

        var list = new List<Entry>();
        foreach (var code in codes) {
            try {
                list.AddRange(code switch {
                    't' => Read(EClass.sources.things.rows.Cast<CardRow>(), "t:"),
                    'c' => Read(EClass.sources.charas.rows.Cast<CardRow>(), "c:"),
                    _ => EmblemSprites.List(code).ToList(),
                });
            } catch (System.Exception e) {
                EmpLog.Information(e, "Emblem grid: the source {Code} could not be listed", code);
            }
        }

        list = list.OrderBy(e => e.Name).ToList();
        if (!stickers) {
            return _lists[key] = list;
        }

        // (a list the sieve's time budget cut is not kept: the next call judges the rest, the verdicts so far kept)
        var kept = StickerSieve.Keep(list, out var complete);
        if (complete) {
            _lists[key] = kept;
        }

        return kept;
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
    ///     <paramref name="onPick" />. <paramref name="fill" />: the frame takes the page's width and, once, the height
    ///     the page leaves under it (the emblem editor: lead dev 2026-11-02, three rows over an empty half page).
    ///     Returns the view, to give it a new list as the filter changes. <paramref name="drop" />: a cell may be
    ///     dragged out of the grid (its picture follows the pointer); on release it is given the entry's id and the
    ///     screen point, and says whether it took it. <paramref name="cell" />: a cell's side (the stickers' grid
    ///     shows its pictures larger on the player's ask), the frame's height stays
    /// </summary>
    internal static SpriteGridView Build(YKLayout parent, Color colour, System.Action<string> onPick, bool fill = false,
        System.Func<string, Vector2, bool>? drop = null, float cell = Cell)
    {
        var frame = new GameObject("SpriteGrid", typeof(RectTransform), typeof(LayoutElement), typeof(RectMask2D), typeof(Image), typeof(ScrollRect));
        frame.transform.SetParent(parent.transform, false);
        var element = frame.GetComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = Rows * Cell;
        element.minWidth = element.preferredWidth = PerRow * Cell;
        element.flexibleWidth = fill ? 1f : 0f;
        // (also by hand: a YK page does not control its children's height, the frame kept Unity's 100)
        ((RectTransform)frame.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Rows * Cell);
        if (fill) {
            frame.AddComponent<TabEmpBase.FillsPage>().Grows = true;
        }

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
        scroll.scrollSensitivity = cell;

        var hover = parent.TextSmall("", FontColor.Gray);
        var view = frame.AddComponent<SpriteGridView>();
        view.Init(scroll, rt, colour, onPick, hover, drop, cell);
        return view;
    }

    internal const float CellSize = Cell;
    internal const int Columns = PerRow;
    internal const int VisibleRows = Rows;
}

/// <summary>
///     The grid's cells: as many as the rows in view and one more, moved and refilled as it scrolls. As many columns
///     as the frame's width holds, centred, and as many rows as its height shows (a frame that fills the page grows)
/// </summary>
internal sealed class SpriteGridView : MonoBehaviour
{
    private readonly List<(RectTransform Rt, Image Back, Image Picture, int Index)> _cells = [];
    private List<SpriteGrid.Entry> _list = [];
    private ScrollRect? _scroll;
    private RectTransform? _content;
    private Color _colour;
    private System.Action<string>? _pick;
    private UIText? _hover;
    private System.Func<string, Vector2, bool>? _drop;
    private RectTransform? _ghost;

    // the entry being dragged (kept from the drag's start: the wheel may scroll its cell to another entry meanwhile)
    private string? _dragged;
    private int _firstRow = -1;
    private int _columns = SpriteGrid.Columns;
    private float _cell = SpriteGrid.CellSize;

    internal int Count => _list.Count;

    internal void Init(ScrollRect scroll, RectTransform content, Color colour, System.Action<string> pick, UIText hover,
        System.Func<string, Vector2, bool>? drop = null, float cell = SpriteGrid.CellSize)
    {
        (_scroll, _content, _colour, _pick, _hover, _drop) = (scroll, content, colour, pick, hover, drop);
        _cell = Mathf.Max(16f, cell);
        Pool(SpriteGrid.Columns * (SpriteGrid.VisibleRows + 1));
        scroll.onValueChanged.AddListener(_ => Refresh());
    }

    /// <summary>The frame changed size (laid out, or grown to the page): the columns and the cells follow</summary>
    private void OnRectTransformDimensionsChange()
    {
        if (_content == null || _scroll == null) {
            return;
        }

        var size = ((RectTransform)transform).rect.size;
        var columns = Mathf.Max(1, Mathf.FloorToInt(size.x / _cell));
        Pool(columns * (Mathf.CeilToInt(size.y / _cell) + 1));
        if (columns != _columns) {
            _columns = columns;
            Show(_list);
            return;
        }

        _firstRow = -1;
        Refresh();
    }

    /// <summary>At least <paramref name="cells" /> cells in the pool</summary>
    private void Pool(int cells)
    {
        if (_content == null) {
            return;
        }

        while (_cells.Count < cells) {
            var go = new GameObject("Cell", typeof(RectTransform), typeof(Image), typeof(Button), typeof(EventTrigger));
            go.transform.SetParent(_content, false);
            go.SetActive(false);
            var crt = (RectTransform)go.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new(0f, 1f);
            crt.sizeDelta = new(_cell - 4f, _cell - 4f);
            var back = go.GetComponent<Image>();
            back.color = new(_colour.r, _colour.g, _colour.b, 0.35f);
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
            // (the cell's trigger already takes the drag from the scroll view: a drag out of the grid drops the entry)
            Add(trigger, EventTriggerType.BeginDrag, data => BeginDrag(slot, data));
            Add(trigger, EventTriggerType.Drag, Dragging);
            Add(trigger, EventTriggerType.EndDrag, EndDrag);
            // (an EventTrigger takes every event, the wheel too: over a cell the grid did not scroll, lead dev
            // 2026-11-02. The wheel is handed on to the grid's scroll view)
            Add(trigger, EventTriggerType.Scroll, Scrolled);
            _cells.Add((crt, back, image, -1));
        }
    }

    private static void Add(EventTrigger trigger, EventTriggerType type, System.Action act) => Add(trigger, type, _ => act());

    private static void Add(EventTrigger trigger, EventTriggerType type, System.Action<BaseEventData> act)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(data => act(data));
        trigger.triggers.Add(entry);
    }

    private void Scrolled(BaseEventData data)
    {
        if (_scroll != null && data is PointerEventData pointer) {
            _scroll.OnScroll(pointer);
        }
    }

    private string? EntryOf(int slot) => _cells[slot].Index is var i and >= 0 && i < _list.Count ? _list[i].Value : null;

    /// <summary>A cell dragged (when the grid has a drop): its picture follows the pointer over the whole screen</summary>
    private void BeginDrag(int slot, BaseEventData data)
    {
        DropGhost();
        _dragged = EntryOf(slot);
        // (Unity's own null test: "?." lets a destroyed object through)
        var canvas = GetComponentInParent<Canvas>();
        var root = canvas != null ? canvas.rootCanvas : null;
        if (_drop == null || _dragged == null || root == null) {
            return;
        }

        var go = new GameObject("StickerGhost", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(root.transform, false);
        go.transform.SetAsLastSibling();
        _ghost = (RectTransform)go.transform;
        _ghost.anchorMin = _ghost.anchorMax = _ghost.pivot = new(0.5f, 0.5f);
        _ghost.sizeDelta = new(_cell, _cell);
        var image = go.GetComponent<Image>();
        image.sprite = _cells[slot].Picture.sprite;
        image.enabled = image.sprite != null;
        image.preserveAspect = true;
        image.raycastTarget = false;
        image.color = new(1f, 1f, 1f, 0.8f);
        Dragging(data);
    }

    private void Dragging(BaseEventData data)
    {
        if (_ghost == null || data is not PointerEventData pointer || _ghost.parent is not RectTransform root) {
            return;
        }

        var canvas = root.GetComponent<Canvas>();
        var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(root, pointer.position, cam, out var local)) {
            _ghost.localPosition = local;
        }
    }

    /// <summary>The drag released: the entry given to the drop, which takes it when it is over its place</summary>
    private void EndDrag(BaseEventData data)
    {
        DropGhost();
        var value = _dragged;
        _dragged = null;
        if (_drop != null && data is PointerEventData pointer && value != null && _drop(value, pointer.position)) {
            SE.ClickGeneral();
        }
    }

    private void DropGhost()
    {
        if (_ghost != null) {
            Destroy(_ghost.gameObject);
            _ghost = null;
        }
    }

    private void OnDisable() => DropGhost();

    /// <summary>A new list (the filter changed): back to the top</summary>
    internal void Show(List<SpriteGrid.Entry> list)
    {
        _list = list;
        if (_content == null || _scroll == null) {
            return;
        }

        var rows = (list.Count + _columns - 1) / _columns;
        _content.sizeDelta = new(0f, rows * _cell);
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

        var first = Mathf.Max(0, Mathf.FloorToInt(_content.anchoredPosition.y / _cell));
        if (first == _firstRow) {
            return;
        }

        _firstRow = first;
        // (the columns centred in the frame)
        var offset = Mathf.Max(0f, (((RectTransform)transform).rect.width - _columns * _cell) / 2f);
        for (var slot = 0; slot < _cells.Count; slot++) {
            var (rt, back, picture, _) = _cells[slot];
            var index = first * _columns + slot;
            if (index >= _list.Count) {
                rt.gameObject.SetActive(false);
                _cells[slot] = (rt, back, picture, -1);
                continue;
            }

            rt.gameObject.SetActive(true);
            var row = index / _columns;
            var col = index % _columns;
            rt.anchoredPosition = new(offset + col * _cell + 2f, -row * _cell - 2f);
            // (cut to its drawn part, cached: only the entries scrolled into view are read)
            var sprite = TeamBanner.Cropped(_list[index].Picture());
            picture.sprite = sprite;
            // (a picture that does not read: an empty cell, not the white square of an image without sprite)
            picture.enabled = sprite != null;
            if (sprite != null) {
                var inner = _cell - 8f;
                var scale = inner / Mathf.Max(sprite.rect.width, sprite.rect.height, 1f);
                picture.rectTransform.sizeDelta = new(sprite.rect.width * scale, sprite.rect.height * scale);
            }

            _cells[slot] = (rt, back, picture, index);
        }
    }

    private void Click(int slot)
    {
        if (EntryOf(slot) is { } value) {
            SE.ClickGeneral();
            _pick?.Invoke(value);
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
