using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

internal abstract class TabEmpBase : YKLayout<LayerCreationData>
{
    internal static Vector2 FitCell(int constraint)
    {
        var scaler = EMono.ui.canvasScaler.scaleFactor;
        // (a share of the screen that the settings panel always holds: at 1/1.7 the row was wider than the panel at
        // 1280x800 and ran under the scroll bar, 2026-10-25)
        return new Vector2(Screen.width / 2f / constraint, 45f) / scaler;
    }

    // ---------------------------------------------------------------- built when first shown

    /// <summary>
    ///     The id of the one tab the window builds when it opens; the others wait for their first showing. Opening
    ///     built all eight, about a second each with 50 players, and every team change opened the window again (user
    ///     2026-10-29 "il ralentit beaucoup le jeu"). Null: every tab is built at once
    /// </summary>
    internal static string? BuildOnly;

    private bool _waiting;

    /// <summary>
    ///     The first line of each tab's OnLayout: true when this tab is not the one shown and waits to be built
    /// </summary>
    protected bool Waits()
    {
        if (BuildOnly is null || BuildOnly == gameObject.name) {
            return false;
        }

        _waiting = true;
        return true;
    }

    /// <summary>
    ///     Builds a waiting tab, now that it is shown
    /// </summary>
    internal void BuildIfWaiting()
    {
        if (!_waiting) {
            return;
        }

        _waiting = false;
        OnLayout();
        UIHelper.WrapTexts(this);
        Relayout();
    }

    private void OnEnable() => BuildIfWaiting();

    // ---------------------------------------------------------------- sub-tabs

    // the sub-tab each page was left on, kept over reopenings while the game runs (never stored)
    private static readonly Dictionary<string, int> LastSubTab = [];
    private const float PillRowHeight = 40f;

    /// <summary>Chooses the sub-tab a page opens on (before it is built)</summary>
    internal static void SetSubTab(string key, int index) => LastSubTab[key] = index;

    // a sub-tab asked for by its label, for a page whose sub-tabs move (World rules: the advanced groups come and go)
    private static readonly Dictionary<string, string> WantedLabel = [];

    /// <summary>Chooses the sub-tab a page opens on by its label (used once, when it is built or shown next)</summary>
    internal static void SetSubTab(string key, string label) => WantedLabel[key] = label;

    private readonly List<GameObject> _subPages = [];
    private readonly List<string> _subLabels = [];
    private string _subKey = "";

    private void TakeWantedLabel()
    {
        if (!WantedLabel.TryGetValue(_subKey, out var label)) {
            return;
        }

        WantedLabel.Remove(_subKey);
        var index = _subLabels.IndexOf(label);
        if (index >= 0) {
            LastSubTab[_subKey] = index;
        }
    }

    /// <summary>
    ///     A page already built, switched to from a link: shows the sub-tab the link chose (SetSubTab)
    /// </summary>
    internal void ShowChosenSubTab()
    {
        if (_subPages.Count == 0) {
            return;
        }

        TakeWantedLabel();
        ShowSubTab(SubTab);
    }

    /// <summary>
    ///     The sub-tab shown now (0 when the page has none)
    /// </summary>
    protected int SubTab => LastSubTab.TryGetValue(_subKey, out var i) && i < _subPages.Count ? i : 0;

    /// <summary>
    ///     Pages of this tab behind a row of the character sheet's tab pills along the bottom of the window (the
    ///     current one highlighted, the choice remembered per tab). Each page is built at once and only shown or
    ///     hidden, so a typed text or an open dropdown survives a switch. With one page there is no row
    /// </summary>
    protected List<YKVertical> SubTabs(string key, IReadOnlyList<(string Label, Action<YKVertical> Build)> pages)
    {
        _subKey = key;
        var built = new List<YKVertical>();
        foreach (var (label, build) in pages) {
            _subLabels.Add(label);
            var page = Vertical();
            page.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            page.LayoutElement().flexibleWidth = 1f;
            page.Layout.spacing = UIHelper.RowSpacing;
            // (content starts at the top and keeps its own height: a short page is no tall empty box)
            page.Layout.childAlignment = TextAnchor.UpperLeft;
            page.Layout.childForceExpandHeight = false;
            build(page);
            _subPages.Add(page.gameObject);
            built.Add(page);
        }

        TakeWantedLabel();
        if (pages.Count > 1) {
            BuildPills(pages.Select(p => p.Label).ToList());
        }

        ShowSubTab(SubTab, false);
        return built;
    }

    /// <summary>
    ///     Shows one page (scrolled to its top) and highlights its pill
    /// </summary>
    protected void ShowSubTab(int index, bool relayout = true)
    {
        if (_subPages.Count == 0) {
            return;
        }

        index = Mathf.Clamp(index, 0, _subPages.Count - 1);
        LastSubTab[_subKey] = index;
        for (var i = 0; i < _subPages.Count; i++) {
            _subPages[i].SetActive(i == index);
        }

        if (_pills != null && index < _pills.list.Count) {
            _pills.Select(_pills.list[index]);
            // (Select does nothing when that pill is already the group's: draw it anyway, a page built hidden never did)
            _pills.RefreshButtons();
            LogPills("shown");
        }

        OnSubTabShown(index);
        if (!relayout) {
            return;
        }

        Relayout();
        if (GetComponentInParent<ScrollRect>() is { } scroll) {
            scroll.verticalNormalizedPosition = 1f;
        }
    }

    /// <summary>
    ///     After a sub-tab is shown (a page that filters its rows does it again here)
    /// </summary>
    protected virtual void OnSubTabShown(int index)
    {
    }

    /// <summary>
    ///     Every page shown at once (a search runs through all of them), or back to the current one
    /// </summary>
    protected void ShowAllSubTabs(bool all, int count = int.MaxValue)
    {
        for (var i = 0; i < _subPages.Count; i++) {
            _subPages[i].SetActive(all && i < count || i == SubTab);
        }
    }

    private UISelectableGroup? _pills;

    private void LogPills(string when)
    {
        if (_pills == null) {
            return;
        }

        var state = string.Join(", ", _pills.list.Select((p, i) =>
            $"{i}{(p == _pills.selected ? "*" : "")}:{(p.isChecked ? "chk" : "-")}/{(p.selected ? "sel" : "-")}/{(p.imageCheck != null ? p.imageCheck.gameObject.activeSelf ? "img" : "noimg" : "null")}/{ColorUtility.ToHtmlStringRGBA(p.image.color)}"));
        EmpLog.Debug($"Sub-tab pills of {GetType().Name} {when}: wanted {_subKey}={SubTab}, group selected {_pills.list.IndexOf(_pills.selected)}, " +
                     $"selectedColor {ColorUtility.ToHtmlStringRGBA(_pills.colorSelected)}, active {gameObject.activeInHierarchy} [{state}]");
    }

    private void BuildPills(List<string> labels)
    {
        // (the row hangs at the bottom of this tab's scroll view, outside the scrolled content, as the character
        // sheet's row hangs under its pages; the view is shortened by the row's height)
        if (GetComponentInParent<ScrollRect>() is not { } scroll || EmpUiParts.Pill is not { } mold) {
            return;
        }

        var holder = new GameObject("SubTabs", typeof(RectTransform));
        holder.SetActive(false);
        var rt = (RectTransform)holder.transform;
        rt.SetParent(scroll.transform, false);
        rt.anchorMin = new(0f, 0f);
        rt.anchorMax = new(1f, 0f);
        rt.pivot = new(0.5f, 0f);
        var column = holder.AddComponent<VerticalLayoutGroup>();
        column.childAlignment = TextAnchor.LowerCenter;
        column.childControlWidth = true;
        column.childControlHeight = false;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;

        var group = holder.AddComponent<UISelectableGroup>();
        group.autoInitialize = false;
        // the character sheet's tab colours: with the default (General) colours the selected pill looked like the others
        // (World rules, Players, 2026-10-28); the group takes them in Awake, which runs when the holder is activated below
        group.colorType = UISelectableGroup.ColorType.Tab;
        group.colorWhenSelected = true;
        group.useTransition = true;
        // the selected pill shows by its button's Selected tint (normal is transparent), and UIButton.OnEnable clears
        // that flag: a page built hidden, or shown again after another tab, drew its pill like the others (the log
        // still said C9E0FF). The game's own switch redraws the group on the frame after it comes back
        group.fixRefreshButtons = true;

        // all on one line when they fit (World rules has nine); else two balanced lines, never one pill alone
        // (the view is not laid out yet when the page is built: the window's own width)
        var width = Mathf.Max(scroll.GetComponent<RectTransform>().rect.width, Layer != null ? Layer.Bound.width : 0f, 400f) - 40f;
        var pills = new List<(UIButton Pill, float Width)>();
        foreach (var label in labels) {
            var pill = Util.Instantiate(mold, holder.transform);
            pill.mainText.SetText(label);
            if (pill.icon != null) {
                pill.icon.gameObject.SetActive(false);
            }
            pill.tooltip.enable = false;
            pill.RebuildLayout(true);
            pills.Add((pill, pill.Rect().rect.width > 1f ? pill.Rect().rect.width : pill.mainText.preferredWidth + 40f));
        }

        var total = pills.Sum(p => p.Width + 2f);
        var split = pills.Count;
        if (total > width && pills.Count > 1) {
            var best = float.MaxValue;
            var first = 0f;
            for (var i = 1; i < pills.Count; i++) {
                first += pills[i - 1].Width + 2f;
                var worst = Mathf.Max(first, total - first);
                if (worst < best) {
                    best = worst;
                    split = i;
                }
            }
        }

        var lines = 0;
        Transform? current = null;
        for (var i = 0; i < pills.Count; i++) {
            if (i == 0 || i == split) {
                var line = new GameObject("Line", typeof(RectTransform)).AddComponent<HorizontalLayoutGroup>();
                line.transform.SetParent(holder.transform, false);
                line.childAlignment = TextAnchor.MiddleCenter;
                line.childControlWidth = false;
                line.childControlHeight = false;
                line.childForceExpandWidth = false;
                line.spacing = 2f;
                line.GetComponent<RectTransform>().sizeDelta = new(0f, PillRowHeight);
                current = line.transform;
                lines++;
            }

            pills[i].Pill.transform.SetParent(current, false);
        }

        var height = lines * PillRowHeight + 4f;
        rt.sizeDelta = new(0f, height);
        rt.anchoredPosition = new(0f, 2f);
        if (scroll.viewport != null) {
            scroll.viewport.offsetMin = new(scroll.viewport.offsetMin.x, scroll.viewport.offsetMin.y + height);
        }
        if (scroll.verticalScrollbar != null && scroll.verticalScrollbar.transform is RectTransform bar) {
            bar.offsetMin = new(bar.offsetMin.x, bar.offsetMin.y + height);
        }

        holder.SetActive(true);
        // (Awake, which takes the colours, does not run while this tab is hidden: take them now or the selected pill
        // is drawn with a blank colour; Players and World rules are built hidden, Play is not)
        group.SetColors();
        group.Init(SubTab, i => ShowSubTab(i));
        _pills = group;
        LogPills("built");
    }

    public override void OnSwitchContent(int idTab)
    {
        base.OnSwitchContent(idTab);
        Relayout();
    }

    /// <summary>
    ///     Lays the page out again, twice and once more on the next frame. One pass of the game's RebuildLayout sizes
    ///     each text before its column has its width, so a description that wraps to two lines kept the height of one
    ///     and ran under the next row (My settings, 2026-10-28); the second pass measures it at its real width
    /// </summary>
    internal void Relayout()
    {
        // (a page built while hidden never drew its selected pill: draw it again now that the tab is shown)
        _pills?.RefreshButtons();
        FitButtons();
        for (var i = 0; i < 2; i++) {
            this.RebuildLayout(true);
            this.RebuildLayoutTo<UIContent>();
        }

        if (isActiveAndEnabled) {
            StartCoroutine(RelayoutLater());
        }
    }

    /// <summary>
    ///     A button of a fixed width is widened to its label once the label is measured: at build time the label's
    ///     width read 0 and a long one ran out of its button ("Take the world from the depot and host it")
    /// </summary>
    private void FitButtons()
    {
        foreach (var button in GetComponentsInChildren<UIButton>(true)) {
            if (button.mainText is not { } text || text.text.Length == 0 ||
                button.GetComponent<LayoutElement>() is not { flexibleWidth: 0f, preferredWidth: >= 80f } element) {
                continue;
            }

            element.preferredWidth = Mathf.Max(element.preferredWidth, text.preferredWidth + 48f);
        }
    }

    private IEnumerator RelayoutLater()
    {
        yield return null;
        _pills?.RefreshButtons();
        FitButtons();
        this.RebuildLayout(true);
        this.RebuildLayoutTo<UIContent>();
    }
}
