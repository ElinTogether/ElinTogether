using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The lands page (hub plan step 5): a card per land of the player's team, with its place, what the team and
///     everyone else may do there, its offline protection and its container log, then the team bank and the player's
///     delivery box. The leader changes the rules; the others see the same drop-downs, greyed
/// </summary>
internal static class TabLands
{
    private static readonly string[] LandIcons = ["icon_container", "icon_influence"];

    /// <summary>
    ///     The Lands page, a sub-tab of Team (user 2026-11-01: fewer tabs; Team only shows it when teams are on)
    /// </summary>
    internal static void Build(YKVertical page)
    {
        var me = EClass.pc.uid;
        var mine = Teams.TeamOf(me);
        // (a player in no team: its own lands, its own rules)
        var lands = Teams.LandsOf(StoryOwner.Of(me)).ToList();
        if (mine == Teams.Wanderers && lands.Count == 0) {
            TabTeam.EmptyPage(page.Card("emp_ui_tab_lands".lang()), LandIcons, "emp_ui_land_noteam".lang(), "emp_ui_land_go_team".lang(),
                () => page.GetComponentInParent<TabTeam>()?.ShowPage(0));
            BuildBoxes(page, mine);
            BuildCommons(page);
            return;
        }

        if (lands.Count == 0) {
            TabTeam.EmptyPage(page.Card("emp_ui_tab_lands".lang()), LandIcons, "emp_ui_land_none".lang());
            BuildBoxes(page, mine);
            BuildCommons(page);
            return;
        }

        // the journal's two pages: the lands (scrolled past five) and the boxes on the left, the picked land's rules
        // on the right (a card per land ran under the window's bottom edge)
        var (left, right) = page.Book();
        var picked = lands.Contains(_picked) ? _picked : lands[0];
        var card = left.Card("emp_ui_tab_lands".lang());
        // (whose lands: the team's emblem and name over them)
        if (TeamBanner.On && mine != Teams.Wanderers) {
            var head = card.Row();
            head.Layout.spacing = 10f;
            TeamBanner.Draw(head, mine, 32f);
            head.Text(Teams.NameOf(mine));
        }

        var list = card.ListArea(lands.Count, 5, 44f);
        foreach (var land in lands) {
            var target = land;
            list.PickLine(Teams.ZoneName(land), Where(land), land == picked, () => {
                _picked = target;
                LayerElinTogether.Instance?.Reopen();
            });
        }

        BuildBoxes(left, mine);
        BuildCommons(left);
        BuildLand(right, picked, mine == Teams.Wanderers || TeamRanks.Can(me, TeamPerm.LandRules, mine));
    }

    // the land whose rules are open on the right, kept over reopenings
    private static int _picked;

    private static string Level(int level) => $"emp_ui_team_rule_{level}".lang();

    /// <summary>
    ///     Where a land is: the zone it lies in, its world map otherwise
    /// </summary>
    private static string Where(int land)
    {
        var spatial = EClass.game.spatials.Find(land);
        return spatial?.parent is { } parent && parent.Name is { Length: > 0 } name ? "emp_ui_land_where".Loc(name) : "";
    }

    private static void BuildLand(YKLayout page, int land, bool leads)
    {
        var card = page.Card(Teams.ZoneName(land));
        if (Where(land) is { Length: > 0 } where) {
            card.TextSmall(where, FontColor.Gray);
        }

        if (!Teams.LandRulesOn) {
            card.TextSmall("emp_ui_land_rules_off".lang(), FontColor.Gray);
        } else {
            var rule = Teams.RuleOf(land);
            var (mineLevel, others) = (Teams.MineLevel(rule), Teams.OthersLevel(rule));
            // (each dropdown under its title, the page's width: beside it, a fixed-width dropdown ran off the page)
            // (the team keeps at least Visit: a land closed to its own team would shut its owners out)
            var own = new List<string> { Level(Teams.Visit), Level(Teams.Use), Level(Teams.Build) };
            card.DropdownSettingRow("emp_ui_team_rule_mine".lang(), null, own, mineLevel - 1,
                i => Teams.Ask(TeamAsk.LandRule, Teams.PackRule(i + 1, others, Teams.OfflineClosed(rule)), land), readOnly: !leads);
            var all = new List<string> { Level(Teams.Closed), Level(Teams.Visit), Level(Teams.Use), Level(Teams.Build) };
            card.DropdownSettingRow("emp_ui_team_rule_others".lang(), null, all, others,
                i => Teams.Ask(TeamAsk.LandRule, Teams.PackRule(mineLevel, i, Teams.OfflineClosed(rule)), land), readOnly: !leads);
            // offline protection: while nobody of the team is in the game, the land is closed to everyone else
            card.ToggleRow("emp_ui_team_rule_offline".lang(), null, Teams.OfflineClosed(rule),
                on => Teams.Ask(TeamAsk.LandRule, Teams.PackRule(mineLevel, others, on), land),
                readOnly: !leads, setByHostText: "emp_ui_team_leader_only_chip".lang());
            if (!leads) {
                card.TextSmall("emp_ui_land_leader_decides".lang(), FontColor.Gray);
            }
        }

        // who opened what on the land (host box "Container access log"); the host checks who may read it
        if (ContainerLog.On) {
            card.ButtonRow().RowButton("emp_ui_container_logs".lang(), () => ContainerLog.Ask(0));
        }
    }

    /// <summary>
    ///     The commons (host box "The start land belongs to everyone"): every player's, nobody's
    /// </summary>
    private static void BuildCommons(YKLayout page)
    {
        var zones = Commons.Zones.ToList();
        if (zones.Count == 0) {
            return;
        }

        var card = page.Card("emp_land_commons".lang(), EmpUi.Icon("icon_influence"));
        foreach (var zone in zones) {
            card.TextSmall("emp_ui_land_commons_line".Loc(zone.Name), FontColor.Gray);
        }
    }

    /// <summary>
    ///     The team bank and the player's delivery box, with the game's own icons for them
    /// </summary>
    private static void BuildBoxes(YKLayout page, int mine)
    {
        var bank = mine == Teams.Wanderers ? null : TeamBoxes.TeamBankBox();
        var delivery = TeamBoxes.DeliveryBox();
        if (bank is null && delivery is null) {
            return;
        }

        // (the header says what is shown: both boxes, or the one there is)
        var header = bank is not null && delivery is not null ? "emp_ui_land_boxes" : bank is not null ? "emp_ui_land_team_bank" : "emp_ui_land_delivery";
        var card = page.Card(header.lang(), EmpUi.Icon("icon_container"));
        if (bank is not null) {
            BoxRow(card, bank, "icon_stash3", "emp_ui_land_team_bank".lang(), "emp_ui_land_bank_line".Loc(bank.GetCurrency(), bank.things.Count));
        }

        if (delivery is not null) {
            if (bank is not null) {
                card.Divider();
            }

            BoxRow(card, delivery, "icon_hotAutoDump", "emp_ui_land_delivery".lang(), "emp_ui_land_items".Loc(delivery.things.Count));
        }
    }

    private static void BoxRow(YKLayout card, Thing box, string icon, string title, string detail)
    {
        var row = card.Row();
        // (the game's own icon for it: the box itself is a hidden container, drawn as a black square)
        if (!row.SpriteIcon(44f, icon)) {
            row.CardIcon(box);
        }

        row.TextColumn(title, detail);
    }
}
