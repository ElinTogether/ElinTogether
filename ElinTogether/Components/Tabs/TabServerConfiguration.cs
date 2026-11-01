using ElinTogether.LangMod;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The rules page ("World rules", step 6 of dev/PLAN_multiplayer_hub.md), the same page for everyone: the host
///     edits, a friend reads what the host told (SettingsTable.FromRules, as before). A banner says who decides,
///     the preset cards and the seven main rules on one page; everything else behind one "Advanced rules" fold that
///     opens the sub-tabs of the groups (user 2026-11-01: clear for a new player, no long texts). A row's explanation
///     is its hover tooltip and a small "?" (it was a grey paragraph under each row). A friend sees only what differs
///     from Recommended. Changes apply at once
/// </summary>
internal class TabServerConfiguration : TabEmpBase
{
    /// <summary>
    ///     Advanced rows shown on the game's mod config page (ModConfigPage): a view of that page only, never stored
    /// </summary>
    internal static bool ShowAdvanced;

    // the view of this page, kept over redraws (a change redraws the layer), never stored
    private static string _search = "";
    private static TabServerConfiguration? _instance;
    private static float _savedY;
    private static int _restore;

    // the advanced rules are folded away until the host opens them; kept over redraws while the game runs, never
    // stored. A jump to a folded row opens them, redraws and then jumps
    private static bool _advancedOpen;
    private static string? _pendingJump;

    private sealed class Line(SettingRow row, GameObject go, string hay, bool changed)
    {
        internal SettingRow Row => row;
        internal GameObject Go => go;
        internal string Hay => hay;
        internal bool Changed => changed;
    }

    private sealed class Group(string id, int page)
    {
        internal string Id => id;
        // (the sub-tab it is on: 0 for the main rules)
        internal int Page => page;
        // (the group's page)
        internal GameObject Card = null!;
        internal string Title = "";
        internal readonly List<Line> Lines = [];
    }

    private readonly List<Group> _groups = [];
    // the rows built on this page, so a row under a choice is indented only when its choice is drawn above it
    private readonly HashSet<string> _built = [];
    private bool _guest;
    private RuleSource _src = RuleSource.Current;
    private RuleSource _target = RuleSource.Defaults;

    // what a greyed row needs, by row id; "!" in front: that row must be off. (Requires only holds the host's own
    // answer, a friend's page must work from what it was told)
    private static readonly Dictionary<string, string[]> Needs = new() {
        ["export_chara"] = ["import_chara"],
        ["player_clock"] = ["combat_time"],
        ["step_pace"] = ["combat_time"],
        ["turn_combat"] = ["!combat_time"],
        ["duel_arena"] = ["duels"],
        ["duel_bets"] = ["duels"],
        ["team_bases"] = ["teams"],
        ["team_approval"] = ["teams"],
        ["team_deed"] = ["teams"],
        ["team_leaders"] = ["teams"],
        ["team_personal"] = ["teams"],
        ["team_pvp"] = ["teams"],
        ["team_bills"] = ["teams"],
        ["team_story"] = ["teams"],
        ["team_quest_sharing"] = ["teams", "personal_quests"],
        ["team_boxes"] = ["teams"],
        ["land_rules"] = ["teams", "team_bases"],
        ["sibling_lands"] = ["teams", "team_bases"],
        ["land_abandon"] = ["teams", "team_bases"],
        ["lands_per_team"] = ["teams", "team_bases"],
        ["abandon_days"] = ["teams", "team_bases", "land_abandon"],
        ["team_max_size"] = ["teams", "team_leaders"],
        ["team_found_cooldown"] = ["teams", "team_leaders"],
        ["spawn_zone"] = ["teams", "independent_travel"],
        ["team_own_guilds"] = ["team_own_story"],
        ["night_passes"] = ["own_sleep"],
        ["living_world"] = ["world_time"],
        ["absent_quests_wait"] = ["personal_quests"],
        ["town_boards"] = ["personal_quests"],
        ["world_map_lead"] = ["world_map_choice"],
        ["look_mods_own"] = ["publish_mods"],
    };

    private static bool IsOfficialFixed(SettingRow row)
    {
        var fixedEntries = SettingsTable.OfficialFixedEntries();
        return row.Entry is { } e && fixedEntries.Contains(e) || row.Choice is { } c && c.Entries.All(fixedEntries.Contains);
    }

    public const string TabName = "emp_tab_server";

    /// <summary>A link to the rules' own first page, whatever sub-tab the page was left on</summary>
    internal const string RulesLink = "emp_tab_rules";

    // the rules' own pages come first; the server's pages (TabServerInfo) after them, in a game
    private int _rulePages = int.MaxValue;

    /// <summary>
    ///     The old Server info tab opens its Status page here (its pages are sub-tabs of World rules now, user
    ///     2026-11-01); RulesLink opens the first page
    /// </summary>
    internal static string Redirect(string tab)
    {
        switch (tab) {
            case TabServerInfo.OldTabName:
                SetSubTab("server", "emp_info_status".lang());
                return TabName;
            case RulesLink:
                SetSubTab("server", 0);
                SetSubTab("server", "emp_ui_sub_presets".lang());
                return TabName;
            default:
                return tab;
        }
    }

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        _instance = this;
        _guest = NetSession.Instance.Transport is ElinNetClient;
        _src = _guest ? SettingsTable.FromRules(NetSession.Instance.Rules) : RuleSource.Current;

        var rows = SettingsTable.Rows().Where(r => !_guest || Told(r)).ToList();
        var preset = Preset(rows);
        // (a custom world is compared with the recommended rules)
        _target = SettingsTable.PresetSource(preset == Custom ? 0 : preset);

        // a friend reads a short summary: what differs from Recommended, not every row read-only
        if (_guest) {
            var guestPages = new List<(string, System.Action<YKVertical>)> {
                ("emp_ui_sub_presets".lang(), page => {
                    BuildBanner(page);
                    BuildPresetCards(page, preset);
                    BuildGuestSummary(page, rows);
                }),
            };
            _rulePages = guestPages.Count;
            AddServerPages(guestPages);
            SubTabs("server", guestPages);
            return;
        }

        // the main page: presets, the seven main rules, then the fold; the groups of advanced rules as sub-tabs only
        // once the fold is open (user, 2026-11-01)
        var advanced = rows.Where(r => r.Advanced).ToList();
        var pages = new List<(string, System.Action<YKVertical>)> {
            ("emp_ui_sub_presets".lang(), page => {
                // the save "Host a game" chose at the title screen: these rules, then Start (TabPlay.HostPick)
                if (!_guest && !EClass.core.IsGameStarted && TabPlay.HostPick is { } pick) {
                    BuildStartHosting(page, pick);
                }

                BuildBanner(page);
                BuildPresetCards(page, preset);
                BuildMain(page, rows.Where(r => !r.Advanced).ToList());
                if (preset == Custom) {
                    BuildDifferences(page, rows);
                }
                BuildAdvancedFold(page, advanced.Count(r => r.Id is not ("checks" or "bills")));
            }),
        };
        if (_advancedOpen) {
            // two groups of rules per pill (nine pills on two lines were not the game's look)
            var filled = SettingsTable.Cards.Where(id => advanced.Any(r => r.Card == id)).ToList();
            for (var i = 0; i < filled.Count; i += 2) {
                var ids = filled.Skip(i).Take(2).ToList();
                var index = pages.Count;
                pages.Add((string.Join(" & ", ids.Select(id => $"emp_ui_sub_{id}".lang())), page => {
                    foreach (var id in ids) {
                        BuildGroup(page, id, index, advanced.Where(r => r.Card == id).ToList());
                    }
                }));
            }
        }

        _rulePages = pages.Count;
        AddServerPages(pages);
        SubTabs("server", pages);
        ApplyFilter();
    }

    /// <summary>
    ///     The old Server info tab's pages, after the rules' own (in a game only: the title screen keeps its tabs)
    /// </summary>
    private static void AddServerPages(List<(string, System.Action<YKVertical>)> pages)
    {
        if (EClass.core.IsGameStarted) {
            pages.AddRange(TabServerInfo.Pages());
        } else {
            // the peer-hosted world's versions and signed lists also at the title (in a game: before Help)
            pages.AddRange(TabWorldAdmin.Pages());
        }
    }

    /// <summary>
    ///     The seven rules a new host decides (who joins, characters, travel, combat pace, building, fights, when the
    ///     host leaves), in one box on the first page
    /// </summary>
    private void BuildMain(YKVertical page, List<SettingRow> rows)
    {
        var group = new Group("main", 0) { Title = "emp_ui_sv_main_title".lang() };
        var box = Vertical(page).Box(group.Title);
        group.Card = box.gameObject;
        box.Layout.spacing = UIHelper.RowSpacing * 2;
        foreach (var row in rows) {
            if (BuildRow(box, row) is { } line) {
                group.Lines.Add(line);
            }
        }

        _groups.Add(group);
    }

    /// <summary>
    ///     "Advanced rules (N) >": opens the sub-tabs of the groups (and their pills); open, it folds them back
    /// </summary>
    private void BuildAdvancedFold(YKVertical page, int count)
    {
        var label = _advancedOpen ? "emp_ui_sv_adv_hide".lang() : "emp_ui_sv_adv_show".Loc(count);
        var button = Vertical(page).ButtonRow().RowButton(label, () => {
            _advancedOpen = !_advancedOpen;
            SetSubTab("server", _advancedOpen ? 1 : 0);
            LayerElinTogether.Instance?.Reopen();
        });
        button.SetTooltipLang("emp_ui_sv_adv_tip");
    }

    /// <summary>
    ///     A friend's page: the rules that differ from Recommended, one line each (label, value, the recommended one);
    ///     a world on Recommended says so in one line
    /// </summary>
    private void BuildGuestSummary(YKLayout page, List<SettingRow> rows)
    {
        var diff = rows.Where(r => r.Under is null && r.Id is not ("checks" or "bills" or "client_mods") &&
                                   IsChanged(r, _src, RuleSource.Defaults)).ToList();
        if (diff.Count == 0) {
            page.Card("emp_ui_sv_diff_title".Loc(0)).Text("emp_ui_sv_guest_all_recommended".lang()).SetSize(UIHelper.LabelSize);
            return;
        }

        var card = page.Card("emp_ui_sv_diff_title".Loc(diff.Count));
        var list = card.ListArea(diff.Count, 10, 24f);
        foreach (var row in diff) {
            var line = list.Horizontal();
            line.Layout.childForceExpandWidth = false;
            line.Layout.childAlignment = TextAnchor.MiddleLeft;
            line.Layout.spacing = 6f;
            var text = line.Text("emp_ui_sv_diff_line".Loc(row.Label, ValueText(row, _src), ValueText(row, RuleSource.Defaults)));
            text.SetSize(UIHelper.LabelSize);
            text.LayoutElement().flexibleWidth = 1f;
            Hint(line, row.Description);
        }
    }

    /// <summary>
    ///     The small "?" after a row's title: its explanation as a hover tooltip, and in a popup on a click (no mouse
    ///     hover on a Steam Deck's touch screen)
    /// </summary>
    private static void Hint(YKLayout parent, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        var hint = parent.Button("?", () => EmpPop.Information(text));
        hint.SetTooltipLang(text);
        var element = hint.LayoutElement();
        element.minWidth = element.preferredWidth = 30f;
        element.preferredHeight = UIHelper.RowHeight - 6f;
        element.flexibleWidth = 0f;
    }

    // the index of Custom in SettingsTable.Presets: it writes nothing, it is what a world is when it matches no preset
    private static readonly int Custom = System.Array.IndexOf(SettingsTable.Presets, "custom");

    // the page of a group of rules: the presets page comes first
    private static int PageOf(Group group) => group.Page;

    protected override void OnSubTabShown(int index)
    {
        if (_search.Trim().Length > 0 && index < _rulePages) {
            ApplyFilter();
        }
    }

    // (a vertical group sizes its height to its content only with this fitter)
    private static YKVertical Vertical(YKLayout parent, bool card = false)
    {
        var box = card ? parent.MakeCard() : parent.Vertical();
        box.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return box;
    }

    /// <summary>
    ///     A friend is told only some rules: the others are not shown (a row it cannot read would say "off")
    /// </summary>
    private bool Told(SettingRow row) => row.Choice is { } c ? c.Entries.Any(e => _src.Raw(e) is not null)
        : row.Id == "client_mods" ? _src.Raw(EmpConfig.Server.ClientOnlyMods) is not null
        : row.Entry is { } e && _src.Raw(e) is not null;

    private static bool IsChanged(SettingRow row, RuleSource src, RuleSource target)
    {
        // (the hidden server is the player's own choice, not a rule of the world; see HostRules)
        if (row.Id is "hidden_server") {
            return false;
        }

        if (row.Choice is { } c) {
            var known = c.Entries.Count(e => src.Raw(e) is not null);
            return known > 0 && (known == c.Entries.Length || c.Read(src) >= 0) && c.Read(src) != c.Read(target);
        }

        return row.Entry is { } e && src.Get(e) is { } v && v != target.Get(e);
    }

    /// <summary>
    ///     The preset the rules match (SettingsTable.Presets, Custom when none)
    /// </summary>
    private int Preset(List<SettingRow> rows)
    {
        var top = rows.Where(r => r.Under is null).ToList();
        for (var i = 0; i < SettingsTable.Presets.Length; i++) {
            if (i != Custom && !top.Any(r => IsChanged(r, _src, SettingsTable.PresetSource(i)))) {
                return i;
            }
        }

        return Custom;
    }

    private static void BuildStartHosting(YKLayout page, GameIndex pick)
    {
        var name = string.IsNullOrEmpty(pick.pcName) ? TabPlay.SaveName(pick) : pick.pcName;
        var card = page.Card("emp_ui_host_start_title".Loc(name));
        card.TextSmall("emp_ui_host_start_desc".lang(), FontColor.Gray);
        var buttons = card.ButtonRow();
        buttons.RowButton("emp_ui_host_start".Loc(name), () => TabPlay.StartHosting(pick));
        buttons.RowButton("emp_ui_host_back".lang(), TabPlay.BackToSaves);
    }

    private void BuildBanner(YKLayout page)
    {
        var host = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.Index == 0)?.User.Name ?? "";
        var card = page.Card(_guest ? "emp_ui_sv_banner_guest".Loc(host) : "emp_ui_sv_banner_host".lang());
        card.TextSmall(_guest ? "emp_ui_rules_note".lang()
            : (NetSession.Instance.InGame ? "emp_ui_sv_note_live" : "emp_ui_sv_note_title").lang(), FontColor.Gray);
        if (_guest && SettingsTable.LastNotice is { } notice) {
            card.TextSmall(notice);
        }
    }

    /// <summary>
    ///     Recommended, Classic and Custom as three cards side by side; the current one is tinted and says so. A
    ///     friend sees them as a read-only answer
    /// </summary>
    private void BuildPresetCards(YKLayout page, int preset)
    {
        var row = Vertical(page).Horizontal();
        row.Layout.childForceExpandWidth = true;
        row.Layout.childControlHeight = true;
        row.Layout.childForceExpandHeight = true;
        row.Layout.spacing = UIHelper.RowSpacing * 1.5f;
        row.Layout.childAlignment = TextAnchor.UpperLeft;
        for (var i = 0; i < SettingsTable.Presets.Length; i++) {
            if (!ShowPreset(i, preset)) {
                continue;
            }

            var index = i;
            var card = Vertical(row, true);
            card.LayoutElement().flexibleWidth = 1f;
            card.LayoutElement().preferredWidth = 0f;
            card.Fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            card.LayoutElement().flexibleHeight = 1f;
            var selected = i == preset;
            if (selected) {
                // the selected card reads as a selected pill: tinted and framed
                var bg = card.Layout.GetComponent<Image>() ?? card.Layout.gameObject.AddComponent<Image>();
                bg.color = new(1f, 0.9f, 0.62f, 0.75f);
                var frame = card.Layout.gameObject.AddComponent<Outline>();
                frame.effectColor = new(0.45f, 0.32f, 0.12f, 0.9f);
                frame.effectDistance = new(2f, -2f);
            }

            // the whole card is the choice (title, chip, paragraph): a selectable card, no button inside. The chip
            // has a line of its own under the title (beside it, it ran over a title that wraps)
            card.Text($"emp_ui_sv_opt_preset_{SettingsTable.Presets[i]}".lang()).SetSize(UIHelper.LabelSize);
            if (selected) {
                var chipLine = card.Horizontal();
                chipLine.Layout.childForceExpandWidth = false;
                chipLine.Layout.childAlignment = TextAnchor.MiddleLeft;
                chipLine.LayoutElement().minHeight = 26f;
                chipLine.Chip("emp_ui_sv_preset_current".lang(), FontColor.Good);
            }

            card.TextSmall($"emp_ui_sv_desc_preset_{SettingsTable.Presets[i]}".lang(), FontColor.Gray);
            if (_guest || !card.Layout.TryGetComponent<Image>(out var face)) {
                continue;
            }

            var pick = card.Layout.gameObject.AddComponent<UnityEngine.UI.Button>();
            pick.targetGraphic = face;
            pick.onClick.AddListener(() => {
                if (index == Custom) {
                    // Custom writes nothing: it opens the advanced rules, to edit by hand
                    if (!_advancedOpen) {
                        _advancedOpen = true;
                        SetSubTab("server", 1);
                        LayerElinTogether.Instance?.Reopen();
                    } else {
                        ShowSubTab(1);
                    }
                    return;
                }

                Capture();
                SettingsTable.ApplyPreset(index);
            });
        }

        if (!_guest && SettingsTable.CanUndo) {
            Vertical(page).ButtonRow().RowButton("emp_ui_sv_undo".lang(), () => {
                Capture();
                SettingsTable.Undo();
            });
        }
    }

    // the index of the public server preset in SettingsTable.Presets
    private static readonly int Official = System.Array.IndexOf(SettingsTable.Presets, "official");

    /// <summary>
    ///     The public server card is for its owner only: a dedicated server (-empserver), or the game that can sign the
    ///     public server; a world already on it shows it too. A friend's host never sees it
    /// </summary>
    private static bool ShowPreset(int index, int current) =>
        index != Official || current == Official || EmpServer.Requested || OfficialServer.CanHost();

    /// <summary>
    ///     What makes the world Custom, as a short list of the rows that differ from the recommended rules; a click
    ///     jumps to the row
    /// </summary>
    private void BuildDifferences(YKLayout page, List<SettingRow> rows)
    {
        var diff = rows.Where(r => r.Under is null && IsChanged(r, _src, RuleSource.Defaults)).ToList();
        var card = page.Card("emp_ui_sv_diff_title".Loc(diff.Count));
        // (under the banner and the preset cards: six lines in view, the others scrolled inside the card)
        var list = card.ListArea(diff.Count, 6, 24f);
        foreach (var row in diff) {
            var id = row.Id;
            // a compact link row, not a full-width button: "Combat pace: Custom (recommended: Each own pace)"
            var link = list.Text("> " + "emp_ui_sv_diff_line".Loc(row.Label, ValueText(row, _src), ValueText(row, RuleSource.Defaults)));
            link.SetSize(UIHelper.LabelSize);
            link.gameObject.AddComponent<UnityEngine.UI.Button>().onClick.AddListener(() => Jump(id));
        }
    }

    private static string ValueText(SettingRow row, RuleSource src) => row.Choice is { } c
        ? c.OptionLabel(c.Read(src))
        : (row.Entry is { } e && src.Get(e) is true ? "emp_ui_rules_yes" : "emp_ui_rules_no").lang();

    /// <summary>
    ///     A group of rules on its own sub-tab, laid out as the game's options window: two framed boxes side by side,
    ///     the main rows on the left, the advanced ones on the right ("Advanced"); a group without advanced rows is cut
    ///     in two halves. A row stays with the rows under it
    /// </summary>
    private void BuildGroup(YKVertical page, string id, int pageIndex, List<SettingRow> rows)
    {
        var group = new Group(id, pageIndex) { Title = $"emp_ui_sv_sec_{id}".lang() };
        // (a page holds two groups: each in a box of its own, so a search can hide one)
        var holder = Vertical(page);
        group.Card = holder.gameObject;
        page = holder;

        // a row and the rows under it, as one block
        var blocks = new List<List<SettingRow>>();
        foreach (var row in rows) {
            if (row.Under is not null && blocks.Count > 0 && blocks[^1].Any(r => r.Id == row.Under)) {
                blocks[^1].Add(row);
            } else {
                blocks.Add([row]);
            }
        }

        var main = blocks.Where(b => !b[0].Advanced).ToList();
        var advanced = blocks.Where(b => b[0].Advanced).ToList();
        string? rightTitle = "emp_ui_sv_adv_title".lang();
        if (advanced.Count == 0 || main.Count == 0) {
            var all = main.Concat(advanced).ToList();
            var half = (all.Sum(b => b.Count) + 1) / 2;
            main = [];
            advanced = [];
            var count = 0;
            foreach (var block in all) {
                (count < half ? main : advanced).Add(block);
                count += block.Count;
            }
            rightTitle = null;
        }

        var (left, right) = page.Stacked();
        Fill(left.Box(group.Title), main);
        if (advanced.Count > 0) {
            Fill(right.Box(rightTitle), advanced);
        }

        _groups.Add(group);
        return;

        void Fill(YKVertical box, List<List<SettingRow>> part)
        {
            box.Layout.spacing = UIHelper.RowSpacing * 2;
            foreach (var row in part.SelectMany(b => b)) {
                if (BuildRow(box, row) is { } line) {
                    group.Lines.Add(line);
                }
            }
        }
    }

    /// <summary>
    ///     One row as the options window draws one: a box setting is the game's checkbox then its title and grey line;
    ///     a choice is its title, the game's dropdown under it, then the grey line. What a greyed row needs is said
    ///     under it; a dot and the revert arrow when it differs from the preset
    /// </summary>
    private Line? BuildRow(YKVertical parent, SettingRow row)
    {
        if (row.Id is "checks" or "bills") {
            return BuildSpecial(parent, row);
        }

        if (row.Id == "client_mods") {
            return BuildClientMods(parent, row);
        }

        var changed = IsChanged(row, _src, _target);
        var block = parent.Vertical();
        block.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        block.Layout.spacing = 2f;
        if (row.Under is not null && _built.Contains(row.Under)) {
            block.Layout.padding = new(UIHelper.SubIndent, 0, 0, 0);
        }
        _built.Add(row.Id);

        var needs = NeedsText(row);
        var enabled = !_guest && needs is null;

        var top = block.Horizontal();
        top.Layout.childForceExpandWidth = false;
        top.Layout.childControlHeight = true;
        top.Layout.childForceExpandHeight = false;
        top.Layout.childAlignment = TextAnchor.UpperLeft;
        top.Layout.spacing = 6f;

        if (row is { Entry: { } entry }) {
            var toggle = top.Toggle("", _src.Get(entry) ?? false, value => {
                Capture();
                SettingsTable.Write([(entry, value)]);
            });
            toggle.mainText.text = "";
            var element = toggle.LayoutElement();
            element.minWidth = element.preferredWidth = 40f;
            element.minHeight = element.preferredHeight = UIHelper.RowHeight;
            element.flexibleWidth = 0f;
            toggle.AsRow();
            toggle.interactable = enabled;
            toggle.SetTooltipLang(row.Description);
        }

        var words = top.Vertical();
        words.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        words.Layout.spacing = 0f;
        words.LayoutElement().flexibleWidth = 1f;
        words.LayoutElement().minWidth = 0f;
        words.Text(row.Label).SetSize(UIHelper.LabelSize);
        Hint(top, row.Description);

        if (changed) {
            MakeDot(top);
            if (!_guest) {
                var revert = top.Button("<", () => Revert(row));
                revert.SetTooltipLang("emp_ui_sv_revert");
                var element = revert.LayoutElement();
                element.minWidth = element.preferredWidth = 34f;
                element.preferredHeight = UIHelper.RowHeight - 6f;
                element.flexibleWidth = 0f;
            }
        }

        // (a box setting's warning stays beside its box, under its title; a choice's goes under its dropdown)
        var below = row.Entry is not null ? words : block;
        if (row is { Choice: { } choice }) {
            var current = choice.Read(_src);
            var options = Enumerable.Range(0, choice.Options.Length).Select(choice.OptionLabel).ToList();
            // (boxes that match no option: shown as such, never rewritten until the host picks one)
            if (current < 0) {
                options.Add(choice.OptionLabel(-1));
            }
            var dropdown = block.Dropdown(options, i => {
                Capture();
                SettingsTable.Write(choice.WriteFor(i));
            }, current < 0 ? options.Count - 1 : current);
            dropdown.LayoutElement().flexibleWidth = 1f;
            dropdown.LayoutElement().preferredHeight = UIHelper.RowHeight;
            dropdown.Rect().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UIHelper.RowHeight);
            dropdown.interactable = enabled;
        }

        // (the explanation is the "?" tooltip: the grey paragraph under every row made the page a wall of text)
        if (needs is not null) {
            below.TextSmall(needs, FontColor.Warning);
        }

        return new(row, block.gameObject, $"{row.Label} {row.Description}", changed);
    }

    /// <summary>
    ///     The joining checks and the tax bills have their own controls (the host only, the page of a friend has no
    ///     line for them)
    /// </summary>
    private Line? BuildSpecial(YKVertical parent, SettingRow row)
    {
        if (_guest) {
            return null;
        }

        var box = Vertical(parent);
        box.Layout.padding = new(row.Under is not null ? UIHelper.SubIndent : 0, 0, 0, 0);
        box.Layout.spacing = UIHelper.RowSpacing;
        if (row.Id == "checks") {
            BuildValidationSets(box);
        } else if (EClass.core.IsGameStarted && NetSession.Instance.Transport is ElinNetHost) {
            // the count of unpaid tax bills against the bills that exist (ElinNetHost.RepairBillCount), in game
            var (count, found) = (EClass.player.taxBills, ElinNetHost.CountTaxBills());
            box.ButtonRow().RowButton("emp_ui_sv_bills".Loc(count, found), () => {
                var off = ElinNetHost.RepairBillCount(true);
                EmpPop.Information((off > 0 ? "emp_ui_sv_bills_done" : "emp_ui_sv_bills_none").Loc(off));
                LayerElinTogether.Instance?.Reopen();
            });
            box.TextSmall("emp_ui_sv_bills_desc".lang(), FontColor.Gray);
        } else {
            Destroy(box.gameObject);
            return null;
        }

        return new(row, box.gameObject, $"{row.Label} {row.Description}", false);
    }

    /// <summary>
    ///     The mods each player may have or not (ClientOnlyMods): a text row, the host types the ids, a friend reads
    ///     them. Written when the host leaves the box (Enter or a click elsewhere): a change draws the page again
    /// </summary>
    private Line BuildClientMods(YKVertical parent, SettingRow row)
    {
        var value = _src.Raw(EmpConfig.Server.ClientOnlyMods) as string ?? "";
        var block = Vertical(parent);
        block.Layout.padding = new(UIHelper.SubIndent, 0, 0, 0);
        block.Layout.spacing = 2f;
        var head = block.Horizontal();
        head.Layout.childForceExpandWidth = false;
        head.Layout.childAlignment = TextAnchor.MiddleLeft;
        head.Layout.spacing = 6f;
        head.Text(row.Label).SetSize(UIHelper.LabelSize);
        Hint(head, row.Description);
        if (_guest) {
            block.TextSmall(value.Trim().Length > 0 ? value : "emp_ui_sv_client_mods_none".lang());
        } else {
            var input = block.InputText(value);
            input.AllowAnyText();
            input.field.characterLimit = 2000;
            input.StyleAsSearch("emp_ui_sv_client_mods_hint".lang());
            input.field.onEndEdit.AddListener(text => {
                var clean = string.Join(",", ClientOnlyMods.Parse(text));
                if (clean != EmpConfig.Server.ClientOnlyMods.Value) {
                    Capture();
                    SettingsTable.Write([(EmpConfig.Server.ClientOnlyMods, clean)]);
                }
            });
        }

        return new(row, block.gameObject, $"{row.Label} {row.Description} {value}", false);
    }

    /// <summary>
    ///     Why a row is greyed ("Needs X"), or null when it is on. Read from what this page shows (a friend's rules
    ///     are the host's), the host's own Requires only for the rows this table does not name
    /// </summary>
    private string? NeedsText(SettingRow row)
    {
        if (!_guest && row.Requires is { } requires && requires()) {
            return null;
        }

        // (the rows the official server fixes: SettingsTable.Rows greys them while this game hosts it)
        if (!_guest && Helper.StoryOwner.OfficialHosting && IsOfficialFixed(row)) {
            return "emp_ui_sv_needs_official".lang();
        }

        if (!Needs.TryGetValue(row.Id, out var ids)) {
            return !_guest && row.Requires is not null ? "emp_ui_sv_needs_other".lang() : null;
        }

        var all = SettingsTable.Rows().ToDictionary(r => r.Id);
        foreach (var raw in ids) {
            var (id, off) = raw.StartsWith('!') ? (raw[1..], true) : (raw, false);
            var met = id == "world_map_choice" ? _src.Raw(EmpConfig.Server.WorldMapTime) is Net.WorldMapTime.PersonalPace
                : all[id].Entry is not { } e || _src.Get(e) is not { } v || v != off;
            if (!met) {
                return (off ? "emp_ui_sv_needs_off" : "emp_ui_sv_needs").Loc(all[id].Label);
            }
        }

        // (a guest is not told what the host's own check says: nothing greyed)
        return _guest ? null : "emp_ui_sv_needs_other".lang();
    }

    private void Revert(SettingRow row)
    {
        var changes = row.Choice is { } c ? c.Entries.Select(e => (e, _target.Raw(e)!)).ToList()
            : row.Entry is { } entry ? [((ConfigEntryBase)entry, _target.Raw(entry)!)] : [];
        Capture();
        SettingsTable.Write(changes);
    }

    /// <summary>
    ///     Searching, folding and the folded rows: shows and hides what is built, then lays the page out again
    /// </summary>
    /// <summary>
    ///     A search shows the matching rows of every group under the search box (all the pages at once); an empty
    ///     search gives back the one page of the sub-tab
    /// </summary>
    private void ApplyFilter()
    {
        var query = _search.Trim();
        // (a search runs through the rules' pages, not the server's: on Status, Message... it waits)
        var searching = query.Length > 0 && SubTab < _rulePages;
        ShowAllSubTabs(searching, _rulePages);
        var any = false;
        foreach (var g in _groups) {
            var hits = 0;
            foreach (var line in g.Lines) {
                var show = !searching || $"{g.Title} {line.Hay}".Contains(query, System.StringComparison.OrdinalIgnoreCase);
                line.Go.SetActive(show);
                hits += show ? 1 : 0;
            }

            if (searching) {
                g.Card.SetActive(hits > 0);
                any |= hits > 0;
            }
        }

        Relayout();
    }

    /// <summary>
    ///     From the list of differences: opens the group and the folded rows, clears the search, scrolls to the row
    /// </summary>
    internal static void ShowRule(string rowId) => _instance?.Jump(rowId);

    private void Jump(string rowId)
    {
        var group = _groups.FirstOrDefault(g => g.Lines.Any(l => l.Row.Id == rowId));
        if (group is null) {
            // a folded advanced row: open the fold, redraw, then jump (OnLayout)
            if (!_guest && !_advancedOpen && SettingsTable.Rows().Any(r => r.Id == rowId && r.Advanced)) {
                _advancedOpen = true;
                _pendingJump = rowId;
                LayerElinTogether.Instance?.Reopen();
            }
            return;
        }

        var line = group.Lines.First(l => l.Row.Id == rowId);
        _search = "";
        ShowSubTab(PageOf(group));
        ApplyFilter();
        if (GetComponentInParent<ScrollRect>() is not { content: { } content } scroll) {
            return;
        }

        Canvas.ForceUpdateCanvases();
        var target = line.Go.GetComponent<RectTransform>();
        var offset = (Vector2)scroll.transform.InverseTransformPoint(content.position) - (Vector2)scroll.transform.InverseTransformPoint(target.position);
        content.anchoredPosition = new(content.anchoredPosition.x, Mathf.Clamp(offset.y - UIHelper.RowHeight, 0f, MaxScroll(scroll)));
    }

    private static float MaxScroll(ScrollRect scroll) => Mathf.Max(0f, scroll.content.rect.height - scroll.viewport.rect.height);

    private static GameObject MakeDot(YKLayout parent)
    {
        var go = new GameObject("changed", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent.transform, false);
        var image = go.GetComponent<Image>();
        // (the engine's round UI sprite; a plain square where it is not loaded)
        image.sprite = UIHelper.FindSprite("Knob");
        image.color = new(0.8f, 0.36f, 0.1f);
        image.raycastTarget = false;
        go.GetComponent<RectTransform>().sizeDelta = new(12f, 12f);
        var element = go.GetComponent<LayoutElement>();
        (element.preferredWidth, element.preferredHeight, element.flexibleWidth) = (12f, 12f, 0f);
        return go;
    }

    /// <summary>
    ///     A change draws the layer again (SettingsTable.Update): keep where the page was scrolled to
    /// </summary>
    private static void Capture()
    {
        if (_instance != null && _instance.GetComponentInParent<ScrollRect>() is { content: { } content }) {
            (_savedY, _restore) = (content.anchoredPosition.y, 6);
        }
    }

    /// <summary>
    ///     Every frame (SettingsTable.Update): puts the page back where Capture saw it once the layer is drawn again
    /// </summary>
    internal static void Tick()
    {
        // a jump to a folded row, once the page drawn again with the fold open is shown (Jump)
        if (_pendingJump is { } jump && _instance != null && _instance.isActiveAndEnabled) {
            _pendingJump = null;
            _instance.Jump(jump);
        }

        if (_restore == 0 || _instance == null || !_instance.isActiveAndEnabled ||
            _instance.GetComponentInParent<ScrollRect>() is not { content: { } content } scroll) {
            return;
        }

        if (--_restore == 0) {
            content.anchoredPosition = new(content.anchoredPosition.x, Mathf.Clamp(_savedY, 0f, MaxScroll(scroll)));
        }
    }

    /// <summary>
    ///     What the preset means, or for Custom how many rows differ from the recommended rules and which (shared
    ///     with ModConfigPage)
    /// </summary>
    internal static string PresetText(int preset)
    {
        if (preset != Custom) {
            return $"emp_ui_sv_desc_preset_{SettingsTable.Presets[preset]}".lang();
        }

        var rows = SettingsTable.Rows().ToDictionary(r => r.Id);
        var diff = SettingsTable.Differences(RuleSource.Current, RuleSource.Defaults);
        return "emp_ui_sv_diff".Loc(diff.Count, string.Join(", ", diff.Select(id => rows[id].Label)));
    }

    private static void BuildValidationSets(YKVertical sets)
    {
        sets.TextSmall("emp_ui_sv_desc_validation".lang());

        var list = sets.Grid()
            .WithConstraintCount(4);
        list.Fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        list.Layout.cellSize = FitCell(4);

        var options = new List<string> {
            "none",
            "sources",
            "plugins",
            "all",
        };

        foreach (var option in options) {
            list.Toggle($"emp_validation_{option}", EmpConfig.Server.SourceValidationSet.Value.Contains(option),
                value => SettingsTable.Write([(EmpConfig.Server.SourceValidationSet, SettingsTable.ToggleCheck(EmpConfig.Server.SourceValidationSet.Value, option, value))]));
        }
    }
}
