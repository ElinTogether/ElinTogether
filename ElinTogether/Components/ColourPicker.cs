using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     A colour picker for the emblem editor (lead dev 2026-11-01): a saturation/value square for the hue, the hue
///     bar, the alpha bar, a hex field (#RRGGBB or #RRGGBBAA), the team's colours and the recent ones. Every change
///     goes to <c>change</c> at once (the editor's preview follows); <c>commit</c> when a drag ends or a value is typed
///     or clicked, which also makes it a recent colour
/// </summary>
internal static class ColourPicker
{
    private const float Square = 150f;
    private const float Bar = 20f;
    private const float Swatch = 20f;
    private const int RecentMax = 12;

    private static readonly List<uint> _recent = [];

    // shared textures, rewritten in place (one picker shows at a time)
    private static Texture2D? _sv;
    private static Texture2D? _hue;
    private static Texture2D? _alpha;
    private static Texture2D? _checker;

    /// <summary>A colour picked: first in the recent row</summary>
    internal static void Remember(uint rgba)
    {
        _recent.Remove(rgba);
        _recent.Insert(0, rgba);
        if (_recent.Count > RecentMax) {
            _recent.RemoveAt(_recent.Count - 1);
        }
    }

    internal static void Build(YKLayout parent, uint start, IReadOnlyList<uint> team, System.Action<uint> change, System.Action<uint> commit)
    {
        var state = new State { Rgba = start };
        state.FromRgba();

        // the square on the left; the preview, the hex field and the two bars beside it
        var top = parent.Horizontal();
        top.Layout.childForceExpandWidth = false;
        top.Layout.childAlignment = TextAnchor.UpperLeft;
        top.Layout.spacing = 12f;

        var sv = Raw(top.transform, "Saturation", Square, Square, SvTexture(state.H));
        state.SvMarker = Marker(sv.transform, 10f, 10f);
        var side = top.Vertical();
        side.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        side.Layout.spacing = 8f;
        side.LayoutElement().flexibleWidth = 1f;

        var previewRow = side.Row();
        state.Preview = Plain(previewRow.transform, "Preview", 40f, 28f);
        var hex = previewRow.InputText(TeamEmblem.HexText(start));
        hex.field.characterLimit = 9;
        hex.LayoutElement().flexibleWidth = 1f;
        state.Hex = hex.field;

        var hue = Raw(side.transform, "Hue", 0f, Bar, HueTexture());
        state.HueMarker = Marker(hue.transform, 4f, Bar + 6f);
        var alphaBack = Raw(side.transform, "Alpha", 0f, Bar, Checker());
        alphaBack.uvRect = new(0f, 0f, 8f, 1f);
        var alpha = Raw(alphaBack.transform, "Gradient", 0f, 0f, AlphaTexture(state.Rgba));
        Stretch(alpha.rectTransform);
        state.AlphaMarker = Marker(alphaBack.transform, 4f, Bar + 6f);
        state.Show();

        void Apply(bool done)
        {
            state.Show();
            change(state.Rgba);
            if (done) {
                Remember(state.Rgba);
                commit(state.Rgba);
            }
        }

        Area(sv.gameObject, p => {
            (state.S, state.V) = (p.x, p.y);
            state.FromHsv();
            Apply(false);
        }, () => Apply(true));
        Area(hue.gameObject, p => {
            state.H = Mathf.Clamp(p.x, 0f, 0.999f);
            SvTexture(state.H);
            state.FromHsv();
            Apply(false);
        }, () => Apply(true));
        Area(alphaBack.gameObject, p => {
            state.A = p.x;
            state.FromHsv();
            Apply(false);
        }, () => Apply(true));
        hex.field.onEndEdit.AddListener(text => {
            if (TeamEmblem.TryHex(text, out var typed) && typed != state.Rgba) {
                state.Set(typed);
                Apply(true);
            } else {
                state.Show();
            }
        });

        SwatchRow(parent, "emp_ui_colour_team".lang(), team, state, Apply);
        // (no recent colour yet: the palette's twelve team colours)
        var recent = _recent.Count > 0 ? _recent : Enumerable.Range(0, TeamBanner.ColorCount).Select(TeamEmblem.Rgba).ToList();
        SwatchRow(parent, "emp_ui_colour_recent".lang(), recent, state, Apply);
    }

    private static void SwatchRow(YKLayout parent, string title, IReadOnlyList<uint> colours, State state, System.Action<bool> apply)
    {
        if (colours.Count == 0) {
            return;
        }

        parent.TextSmall(title, FontColor.Gray);
        var line = parent.Row();
        line.Layout.spacing = 4f;
        line.LayoutElement().minHeight = Swatch + 4f;
        foreach (var rgba in colours.Distinct().Take(RecentMax)) {
            var c = rgba;
            TeamBanner.Swatch(line, ToColor(c), Swatch, Swatch, false, () => {
                state.Set(c);
                apply(true);
            });
        }
    }

    private sealed class State
    {
        internal uint Rgba;
        internal float H, S, V, A;
        internal RectTransform? SvMarker, HueMarker, AlphaMarker;
        internal Image? Preview;
        internal InputField? Hex;

        internal void Set(uint rgba)
        {
            var hue = H;
            Rgba = rgba;
            FromRgba();
            if (Mathf.Abs(hue - H) > 0.0001f) {
                SvTexture(H);
            }
        }

        internal void FromRgba()
        {
            var c = ToColor(Rgba);
            Color.RGBToHSV(c, out H, out S, out V);
            A = c.a;
        }

        internal void FromHsv()
        {
            var c = Color.HSVToRGB(H, S, V);
            c.a = A;
            Rgba = ToRgba(c);
        }

        internal void Show()
        {
            SetAnchor(SvMarker, S, V);
            SetAnchor(HueMarker, H, 0.5f);
            SetAnchor(AlphaMarker, A, 0.5f);
            if (Preview != null) {
                Preview.color = ToColor(Rgba);
            }

            Hex?.SetTextWithoutNotify(TeamEmblem.HexText(Rgba));
            AlphaTexture(Rgba);
        }
    }

    private static void SetAnchor(RectTransform? rt, float x, float y)
    {
        if (rt != null) {
            rt.anchorMin = rt.anchorMax = new(Mathf.Clamp01(x), Mathf.Clamp01(y));
        }
    }

    internal static Color ToColor(uint rgba) => new Color32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    internal static uint ToRgba(Color c)
    {
        Color32 b = c;
        return ((uint)b.r << 24) | ((uint)b.g << 16) | ((uint)b.b << 8) | b.a;
    }

    // ---------------------------------------------------------------- parts

    private static RawImage Raw(Transform parent, string name, float width, float height, Texture texture)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement), typeof(RawImage));
        go.transform.SetParent(parent, false);
        var element = go.GetComponent<LayoutElement>();
        if (width > 0f) {
            (element.minWidth, element.preferredWidth, element.flexibleWidth) = (width, width, 0f);
        } else {
            element.flexibleWidth = 1f;
        }

        if (height > 0f) {
            element.minHeight = element.preferredHeight = height;
        }

        var image = go.GetComponent<RawImage>();
        image.texture = texture;
        return image;
    }

    private static Image Plain(Transform parent, string name, float width, float height)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        go.transform.SetParent(parent, false);
        var element = go.GetComponent<LayoutElement>();
        (element.minWidth, element.preferredWidth, element.minHeight, element.preferredHeight, element.flexibleWidth) = (width, width, height, height, 0f);
        var image = go.GetComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    /// <summary>A small white frame with a dark edge, placed by its anchor (0..1 of the parent)</summary>
    private static RectTransform Marker(Transform parent, float width, float height)
    {
        var go = new GameObject("Marker", typeof(RectTransform), typeof(Image), typeof(Outline));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new(width, height);
        rt.pivot = new(0.5f, 0.5f);
        var image = go.GetComponent<Image>();
        image.color = new(1f, 1f, 1f, 0.9f);
        image.raycastTarget = false;
        var outline = go.GetComponent<Outline>();
        outline.effectColor = new(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new(1f, -1f);
        return rt;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static void Area(GameObject go, System.Action<Vector2> move, System.Action up)
    {
        var area = go.AddComponent<PointerArea>();
        area.Move = move;
        area.Up = up;
    }

    // ---------------------------------------------------------------- textures

    private static Texture2D New(int w, int h, FilterMode filter) =>
        new(w, h, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp, name = "emp_colour_picker" };

    private static Texture2D SvTexture(float hue)
    {
        const int n = 32;
        if (_sv == null) {
            _sv = New(n, n, FilterMode.Bilinear);
        }

        var px = new Color32[n * n];
        for (var y = 0; y < n; y++) {
            for (var x = 0; x < n; x++) {
                px[y * n + x] = Color.HSVToRGB(hue, x / (n - 1f), y / (n - 1f));
            }
        }

        _sv.SetPixels32(px);
        _sv.Apply(false, false);
        return _sv;
    }

    private static Texture2D HueTexture()
    {
        if (_hue != null) {
            return _hue;
        }

        const int n = 64;
        _hue = New(n, 1, FilterMode.Bilinear);
        var px = new Color32[n];
        for (var x = 0; x < n; x++) {
            px[x] = Color.HSVToRGB(x / (n - 1f), 1f, 1f);
        }

        _hue.SetPixels32(px);
        _hue.Apply(false, false);
        return _hue;
    }

    private static Texture2D AlphaTexture(uint rgba)
    {
        const int n = 32;
        if (_alpha == null) {
            _alpha = New(n, 1, FilterMode.Bilinear);
        }

        var c = (Color32)ToColor(rgba);
        var px = new Color32[n];
        for (var x = 0; x < n; x++) {
            px[x] = new(c.r, c.g, c.b, (byte)(x * 255 / (n - 1)));
        }

        _alpha.SetPixels32(px);
        _alpha.Apply(false, false);
        return _alpha;
    }

    private static Texture2D Checker()
    {
        if (_checker != null) {
            return _checker;
        }

        _checker = New(2, 2, FilterMode.Point);
        _checker.wrapMode = TextureWrapMode.Repeat;
        Color32 light = new(200, 200, 200, 255), dark = new(120, 120, 120, 255);
        _checker.SetPixels32([light, dark, dark, light]);
        _checker.Apply(false, false);
        return _checker;
    }
}

/// <summary>
///     A pressed or dragged point of a rectangle, from 0 to 1 left to right and bottom to top; released
/// </summary>
internal sealed class PointerArea : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IPointerUpHandler
{
    internal System.Action<Vector2>? Move;
    internal System.Action? Up;

    private Vector2 Norm(PointerEventData data)
    {
        var rt = (RectTransform)transform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, data.position, data.pressEventCamera, out var p);
        var r = rt.rect;
        return new(Mathf.Clamp01((p.x - r.xMin) / Mathf.Max(1f, r.width)), Mathf.Clamp01((p.y - r.yMin) / Mathf.Max(1f, r.height)));
    }

    public void OnPointerDown(PointerEventData data) => Move?.Invoke(Norm(data));

    public void OnBeginDrag(PointerEventData data)
    {
    }

    public void OnDrag(PointerEventData data) => Move?.Invoke(Norm(data));

    public void OnPointerUp(PointerEventData data) => Up?.Invoke();
}
