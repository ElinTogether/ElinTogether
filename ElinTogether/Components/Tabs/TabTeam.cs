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
///     The team page (hub plan step 5, council 2026-10-28): the same in every game, the host's own included, and its
///     tab is always there. The player's team and its members, the leader's tools, the requests and invitations, and
///     the choices the game's player list already offered (found, join, leave, accept, refuse), asked of the game that
///     keeps the book (<see cref="Teams.Ask" />). Its lands are on the next page (<see cref="TabLands" />)
/// </summary>
internal class TabTeam : TabEmpBase
{
    /// <summary>
    ///     Teams exist in this game: a connected game whose rules have them on
    /// </summary>
    internal static bool TeamsOn => NetSession.Instance is { InGame: true, Rules.UseTeams: true } && EClass.pc != null;

    /// <summary>
    ///     "Teams are off in this world": the empty state both pages show. Only the host gets the button that opens the
    ///     World rules page at the Teams row
    /// </summary>
    internal static void BuildOff(YKLayout page, string titleId)
    {
        var card = page.Card(titleId.lang());
        if (!NetSession.Instance.InGame) {
            EmptyPage(card, TeamIcons, "emp_ui_team_unplugged".lang(), "emp_ui_players_go_play".lang(),
                () => LayerElinTogether.Instance?.Window.SwitchContent(TabPlay.TabName));
        } else if (NetSession.Instance.Transport is ElinNetHost) {
            EmptyPage(card, TeamIcons, "emp_ui_team_off".lang(), "emp_ui_team_turn_on".lang(), () => {
                LayerElinTogether.Instance?.Open(TabServerConfiguration.RulesLink);
                TabServerConfiguration.ShowRule("teams");
            });
        } else {
            EmptyPage(card, TeamIcons, "emp_ui_team_off_guest".lang());
        }
    }


    internal static readonly string[] TeamIcons = ["icon_factions", "icon_medal"];

    /// <summary>
    ///     An empty page: a game icon, a two-line explanation and, when there is something to do, one button as wide as
    ///     its label
    /// </summary>
    internal static void EmptyPage(YKLayout card, string[] icons, string message, string? buttonText = null, System.Action? act = null)
    {
        var box = card.Vertical();
        box.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        box.Layout.childAlignment = TextAnchor.MiddleCenter;
        box.Layout.padding = new(16, 16, 10, 10);
        box.Layout.spacing = UIHelper.RowSpacing;

        var iconRow = box.Horizontal();
        iconRow.Layout.childForceExpandWidth = false;
        iconRow.Layout.childAlignment = TextAnchor.MiddleCenter;
        iconRow.SpriteIcon(48f, icons);

        box.Text(message, FontColor.Gray).alignment = TextAnchor.MiddleCenter;
        if (buttonText == null || act == null) {
            return;
        }

        var row = box.Horizontal();
        row.Layout.childForceExpandWidth = false;
        row.Layout.childAlignment = TextAnchor.MiddleCenter;
        var button = row.Button(buttonText, act);
        var element = button.LayoutElement();
        element.preferredWidth = Mathf.Max(UIHelper.ButtonWidth, (button.mainText != null ? button.mainText.preferredWidth : 0f) + 48f);
        element.flexibleWidth = 0f;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text.Substring(1);

    public const string TabName = "emp_tab_team";

    /// <summary>The Lands sub-tab's place on this page (the Lands tab became that sub-tab, user 2026-11-01)</summary>
    internal const int LandsPage = 1;

    /// <summary>The old Lands tab's name opens the Lands sub-tab of this page</summary>
    internal static string Redirect(string tab)
    {
        if (tab != "emp_tab_lands") {
            return tab;
        }

        SetSubTab("team", LandsPage);
        return TabName;
    }

    internal void ShowPage(int index) => ShowSubTab(index);

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        if (!TeamsOn) {
            BuildOff(this, "emp_ui_team_title");
            return;
        }

        // the journal's two pages: the team and its members on the left, the picked member and the leader's tools
        // on the right (without a team: founding and invitations on the left, the other teams on the right)
        var me = EClass.pc.uid;
        var mine = Teams.TeamOf(me);
        var pages = new List<(string, System.Action<YKVertical>)> {
            ("emp_ui_team_tab_team".lang(), page => BuildTeamPage(page, me, mine)),
            // the lands, always second (LandsPage: the old Lands tab's links open it)
            ("emp_ui_tab_lands".lang(), TabLands.Build),
        };
        // the emblem has its own page, laid out to fit the window (it sat under the team page, out of reach). Teams
        // only: a player alone founds a team of one for an emblem (user 2026-10-30)
        if (mine != Teams.Wanderers && TeamBanner.On) {
            pages.Add(("emp_ui_team_tab_emblem".lang(), page => {
                var (left, right) = page.Book();
                EmblemEditor.Build(left, right, mine, Teams.LeaderOf(mine) == me);
            }));
        }

        // a member's page holds the team and its tools: the other teams, and the team's map and journal, have their
        // own pages so each fits the window (they ran under its bottom edge)
        if (mine != Teams.Wanderers) {
            pages.Add(("emp_ui_team_others".lang(), page => {
                var (left, right) = page.Book();
                BuildOtherTeams(left, mine);
                BuildTeamLines(right);
            }));
            if (TeamBanner.On) {
                pages.Add(("emp_ui_team_journal".lang(), page => {
                    var (left, right) = page.Book();
                    BuildBannerCard(left, right, mine);
                }));
            }
        }

        SubTabs("team", pages);
    }

    private void BuildTeamPage(YKVertical page, int me, int mine)
    {
        var (left, right) = page.Book();
        TeamOfferCard.Build(left, me);
        BuildInvitation(left, me);
        if (mine == Teams.Wanderers) {
            BuildFound(left);
            // (both on the right page: a few rows of each, scrolled inside their cards)
            BuildOtherTeams(right, mine, 3);
            BuildTeamLines(right, 4);
        } else {
            BuildOwnTeam(left, right, mine, me);
        }
    }

    // the member whose page is open on the right, kept over reopenings
    private static int _picked;

    private static Chara? CharaOf(int uid) => Teams.FindChara(uid);

    /// <summary>
    ///     A line with a text and the two answers of a request or an invitation
    /// </summary>
    private static void AnswerRow(YKLayout card, string text, System.Action accept, System.Action refuse)
    {
        // (the words on their own line, the two answers under them: beside two buttons they had a third of the page)
        card.Text(text);
        var row = card.ButtonRow();
        row.RowButton("emp_ui_team_invite_accept".lang(), accept);
        row.RowButton("emp_ui_team_invite_refuse".lang(), refuse);
    }

    /// <summary>
    ///     A team invited this player: a row with the two answers (no popup)
    /// </summary>
    private static void BuildInvitation(YKLayout page, int me)
    {
        var team = Teams.InviteOf(me);
        if (team == Teams.Wanderers) {
            return;
        }

        var card = page.Card("emp_ui_team_invite_title".lang());
        AnswerRow(card, "emp_ui_team_invite_line".Loc(Teams.NameOf(team), Teams.PlayerName(Teams.LeaderOf(team))),
            () => Teams.Ask(TeamAsk.AcceptInvite, team), () => Teams.Ask(TeamAsk.RefuseInvite, team));
    }

    private static void BuildFound(YKLayout page)
    {
        var card = page.Card("emp_ui_team_title".lang());
        EmptyPage(card, TeamIcons, "emp_ui_team_none".lang(), "emp_team_found".lang(), () => {
            Dialog.InputName("emp_ui_team_name_ask", EClass.pc.NameSimple, (cancel, text) => {
                if (!cancel) {
                    Teams.Ask(TeamAsk.Found, text: text);
                }
            }).input.field.characterLimit = Teams.NameMax;
        });
    }

    private static void BuildOwnTeam(YKVertical left, YKVertical right, int mine, int me)
    {
        var leader = Teams.LeaderOf(mine);
        var leads = leader == me;
        var manages = leads && Teams.LeadersManage;
        var members = Teams.MembersOfTeam(mine).ToList();

        // the header: the team's name, then its facts as the character sheet lists them
        var card = left.Card(Teams.NameOf(mine));
        // (the emblem beside the facts, as a portrait beside a sheet's: not on a line of its own over them)
        YKLayout facts = card;
        if (TeamBanner.On) {
            var head = card.Row();
            head.Layout.childAlignment = TextAnchor.UpperLeft;
            head.Layout.spacing = 14f;
            head.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            TeamBanner.Draw(head, mine);
            var column = head.Vertical();
            column.Layout.childControlHeight = false;
            column.Layout.childForceExpandHeight = false;
            column.Layout.spacing = 2f;
            column.LayoutElement().flexibleWidth = 1f;
            facts = column;
        }
        facts.KeyValue("emp_ui_kv_leader".lang(), Teams.PlayerName(leader));
        facts.KeyValue("emp_ui_kv_members".lang(), members.Count.ToString());
        facts.KeyValue("emp_ui_kv_joining".lang(), (Teams.IsOpen(mine) ? "emp_ui_team_chip_open" : "emp_ui_team_chip_closed").lang());
        if (TeamBoxes.TeamBankGold() is { } bank) {
            facts.KeyValue("emp_ui_kv_bank".lang(), bank.ToString());
        }
        if (TeamBills.On) {
            facts.KeyValue("emp_ui_kv_bills".lang(), TeamBills.UnpaidOf(mine).ToString());
        }
        if (TeamStory.On) {
            facts.KeyValue("emp_ui_kv_story".lang(), TeamStory.ChapterOf(mine).ToString());
        }

        // the members: a row each (scrolled past three); a click opens the member's page on the right
        var card2 = left.Card("emp_ui_kv_members".lang());
        var list = card2.ListArea(members.Count, 3, MemberRowHeight);
        var picked = members.Contains(_picked) ? _picked : me;
        foreach (var member in members) {
            var chara = CharaOf(member);
            var target = member;
            // the "..." of a member the leader manages: the same two choices as the member's page, one click nearer
            var actions = new List<(string, System.Action)>();
            if (TeamSheet.On) {
                actions.Add(("emp_ui_team_sheet".lang(), () => TeamSheet.Ask(target)));
            }
            if (manages && member != me) {
                actions.Add(("emp_ui_team_promote".lang(), () => Teams.Ask(TeamAsk.Promote, target)));
                actions.Add(("emp_ui_team_kick".lang(), () => Teams.Ask(TeamAsk.Kick, target)));
            }
            var row = list.PickRow(new PlayerRowInfo(Teams.PlayerName(member), MemberLine(chara, member), chara), member == picked, () => {
                _picked = target;
                LayerElinTogether.Instance?.Reopen();
            }, actions);
            if (member == leader) {
                AddCrown(row);
            }
        }

        // host box "Teammates share quests": each player's own choice, kept by the host
        if (TeamQuests.On) {
            card2.ToggleRow("emp_ui_team_share_quests".lang(), "emp_ui_team_share_quests_desc".lang(), TeamQuests.Shares(me),
                TeamQuests.Ask, ownLines: true);
        }

        card2.ButtonRow().RowButton("emp_team_leave".Loc(Teams.NameOf(mine)), () => Teams.Ask(TeamAsk.Leave));

        // the leader answers who asks (the choices of the player list moved here), at the top of the right page
        if (leads) {
            var asking = Teams.Asking(mine).ToList();
            if (asking.Count > 0) {
                var requests = right.Card("emp_ui_team_requests".lang());
                var area = requests.ListArea(asking.Count, 2, 70f);
                foreach (var asker in asking) {
                    var target = asker;
                    AnswerRow(area, "emp_ui_team_asks_row".Loc(Teams.PlayerName(asker)),
                        () => Teams.Ask(TeamAsk.Accept, target), () => Teams.Ask(TeamAsk.Refuse, target));
                }
            }
        }

        BuildMemberPage(right, picked, leader, me, manages);
        BuildTools(right, mine, leads, manages);
    }

    private const float MemberRowHeight = 60f;

    /// <summary>
    ///     Host box "Team banners": the map of the team's lands on the left page, the team journal on the right one
    ///     (scrolled inside its card)
    /// </summary>
    private static void BuildBannerCard(YKLayout left, YKLayout right, int mine)
    {
        var lands = left.Card("emp_ui_team_lands_map".lang());
        TeamBanner.BuildLandMap(lands, mine);

        var journal = right.Card("emp_ui_team_journal".lang());
        // the activity journal's entries of this team, newest first (box "Activity journal")
        var lines = ActivityJournal.On
            ? ActivityJournal.Entries().Where(e => e.Team == mine).Reverse().Take(50).ToList()
            : [];
        if (lines.Count == 0) {
            journal.TextSmall("emp_ui_team_journal_none".lang(), FontColor.Gray);
        }

        var list = journal.ListArea(lines.Count, 16, 18f);
        foreach (var entry in lines) {
            list.TextSmall(ActivityJournal.Line(entry));
        }
    }

    /// <summary>
    ///     The right page: the picked member's facts and, for a leader who manages the team, promote and remove
    /// </summary>
    private static void BuildMemberPage(YKVertical page, int member, int leader, int me, bool manages)
    {
        var chara = CharaOf(member);
        var card = page.Card(Teams.PlayerName(member));
        // the portrait on the left, the facts beside it (the Players page's head): one block, not two
        var head = card.Row();
        head.Layout.childAlignment = TextAnchor.UpperLeft;
        head.Layout.spacing = 14f;
        head.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        if (chara != null) {
            head.Portrait(chara);
        }
        var facts = head.Vertical();
        facts.Layout.childControlHeight = false;
        facts.Layout.childForceExpandHeight = false;
        facts.Layout.spacing = 2f;
        facts.LayoutElement().flexibleWidth = 1f;
        facts.KeyValue("emp_ui_kv_character".lang(), chara?.Name);
        facts.KeyValue("emp_ui_kv_level".lang(), chara?.LV.ToString());
        facts.KeyValue("emp_ui_kv_state".lang(), Cap((Teams.IsOnline(member) ? "emp_ui_team_online" : "emp_ui_team_offline").lang()));
        facts.KeyValue("emp_ui_kv_role".lang(), member == leader ? "emp_ui_team_crown".lang() : "emp_ui_role_player".lang());

        // what can be done, as wide as the labels, side by side
        var actions = new List<(string, System.Action)>();
        if (TeamSheet.On) {
            actions.Add(("emp_ui_team_sheet".lang(), () => TeamSheet.Ask(member)));
        }
        if (manages && member != me) {
            actions.Add(("emp_ui_team_promote".lang(), () => Teams.Ask(TeamAsk.Promote, member)));
            actions.Add(("emp_ui_team_kick".lang(), () => Teams.Ask(TeamAsk.Kick, member)));
        }
        card.ButtonFlow(actions);
    }

    private static string MemberLine(Chara? chara, int member)
    {
        var state = (Teams.IsOnline(member) ? "emp_ui_team_online" : "emp_ui_team_offline").lang();
        return chara != null ? "emp_ui_team_detail".Loc(chara.LV, state) : state;
    }

    /// <summary>
    ///     A crown by the leader's name: the game's own sprite when it has one, a "Leader" chip otherwise
    /// </summary>
    private static void AddCrown(YKHorizontal row)
    {
        var before = row.transform.childCount;
        if (!row.SpriteIcon(22f, "icon_medal")) {
            row.Chip("emp_ui_team_crown".lang());
        }

        // (before the "..." button, after the name)
        if (row.transform.childCount > before) {
            row.transform.GetChild(row.transform.childCount - 1).SetSiblingIndex(2);
        }
    }

    /// <summary>
    ///     The leader's tools in one card (host box "Team leaders manage their team"): rename, who may join, invite the
    ///     players in the game who have no team, end the team. Grey, with one line saying why, for anyone else
    /// </summary>
    private static void BuildTools(YKLayout page, int mine, bool leads, bool manages)
    {
        var card = page.Card("emp_ui_team_manage".lang());
        if (!manages) {
            card.TextSmall((leads ? "emp_ui_team_tools_host_off" : "emp_ui_team_tools_leader_only").lang(), FontColor.Gray);
        }

        card.ToggleRow("emp_ui_team_joining".lang(), "emp_ui_team_joining_desc".lang(), Teams.IsOpen(mine),
            on => Teams.Ask(TeamAsk.SetOpen, on ? 1 : 0), readOnly: !manages, setByHostText: "emp_ui_team_leader_only_chip".lang(), ownLines: true);

        // (a player in the game, in no team and not yet asked: the host checks the same again)
        var open = manages
            ? NetSession.Instance.CurrentPlayers.Select(p => p.CharaUid).Distinct()
                .Where(uid => Teams.TeamOf(uid) != mine && Teams.InviteOf(uid) == Teams.Wanderers).ToList()
            : [];
        // the invitations not yet answered, on one line
        var invited = manages ? Teams.InvitedBy(mine).ToList() : [];
        if (invited.Count > 0) {
            card.TextSmall("emp_ui_team_invited_line".Loc(string.Join(", ", invited.Select(Teams.PlayerName))), FontColor.Gray);
        }

        // rename and invite side by side, as wide as their labels; one Invite button opens the list of who may be asked
        var buttons = card.ButtonRow();
        buttons.RowButton("emp_ui_team_rename".lang(), () => {
            Dialog.InputName("emp_ui_team_rename_ask", Teams.NameOf(mine), (cancel, text) => {
                if (!cancel) {
                    Teams.Ask(TeamAsk.Rename, text: text);
                }
            }).input.field.characterLimit = Teams.NameMax;
        }).interactable = manages;
        UIButton? invite = null;
        invite = buttons.RowButton("emp_ui_team_invite_any".lang(), () => InvitePicker(invite, open));
        invite.interactable = manages && open.Count > 0;
        if (manages && open.Count == 0) {
            invite.SetTooltipLang("emp_ui_team_invite_none");
        }

        card.ButtonRow().RowButton("emp_ui_team_disband".lang(), () => {
            Dialog.YesNo("emp_ui_team_disband_confirm".Loc(Teams.NameOf(mine)), () => Teams.Ask(TeamAsk.Disband));
        }).interactable = manages;
    }

    /// <summary>
    ///     The players who may be invited, in the game's own context menu under the button (by groups of fifteen in
    ///     sub-menus when there are more: one long menu would run off the screen)
    /// </summary>
    private static void InvitePicker(UIButton? anchor, List<int> players)
    {
        if (players.Count == 0) {
            return;
        }

        var menu = EClass.ui.CreateContextMenuInteraction();
        if (anchor != null) {
            menu.SetHighlightTarget(anchor);
        }

        var sorted = players.Select(uid => (Uid: uid, Name: Teams.PlayerName(uid)))
            .OrderBy(p => p.Name, System.StringComparer.OrdinalIgnoreCase).ToList();
        const int group = 15;
        for (var i = 0; i < sorted.Count; i += group) {
            var chunk = sorted.Skip(i).Take(group).ToList();
            var into = sorted.Count <= group ? menu : menu.AddChild($"{chunk[0].Name} - {chunk[chunk.Count - 1].Name}");
            foreach (var (uid, name) in chunk) {
                var target = uid;
                into.AddButton(name, () => Teams.Ask(TeamAsk.Invite, target));
            }
        }

        menu.Show();
    }

    private static void BuildOtherTeams(YKLayout page, int mine, int maxRows = 6)
    {
        // (never the player's own team; a team with nobody left in it is a ghost of the book, not one to join)
        var others = Teams.Ids().Where(id => id != Teams.Wanderers && id != mine && Teams.MembersOfTeam(id).Any()).ToList();
        if (others.Count == 0) {
            return;
        }

        var card = page.Card("emp_ui_team_others".lang());
        // more than a few teams: a frame of a fixed height that scrolls, not a page that grows
        var list = card.ListArea(others.Count, maxRows, TeamRowHeight);
        foreach (var team in others) {
            var row = list.Row();
            var element = row.LayoutElement();
            element.minHeight = element.preferredHeight = TeamRowHeight;
            TeamBanner.Draw(row, team, 40f);
            row.TextColumn(Clip(Teams.NameOf(team), 24),
                "emp_ui_team_leader_short".Loc(Clip(Teams.PlayerName(LeaderOrFirst(team)), 16), Teams.MembersOfTeam(team).Count()));
            var id = team;
            // (the joining rule is the button's hover text, said once instead of on every row)
            var join = row.Button("emp_ui_team_join_short".lang(), () => Teams.Ask(TeamAsk.Join, id));
            join.SetTooltipLang(Teams.IsOpen(team) ? "emp_ui_team_chip_open" : "emp_ui_team_chip_closed");
            var le = join.LayoutElement();
            le.preferredWidth = 90f;
            le.flexibleWidth = 0f;
        }
    }

    // the leader's character, or the first member's when the book names none
    private static int LeaderOrFirst(int team) => Teams.LeaderOf(team) is var l && l != 0 ? l : Teams.MembersOfTeam(team).FirstOrDefault();

    private const float TeamRowHeight = 54f;

    /// <summary>
    ///     The bills and story chapter of every team (host options "Teams pay their own land" and "Teams keep their own
    ///     story journal"), moved here from the session page: the name, its figures in grey under it (one line each,
    ///     it wrapped to two), scrolled past a few
    /// </summary>
    private static void BuildTeamLines(YKLayout page, int maxRows = 8)
    {
        if (!TeamBills.On && !TeamStory.On) {
            return;
        }

        var card = page.Card("emp_ui_teams_title".lang());
        // (the teams; a player in no team sees its own line first, never one shared by every player in no team)
        var ids = Teams.Ids().Where(id => id != Teams.Wanderers).ToList();
        if (Teams.TeamOf(EClass.pc.uid) == Teams.Wanderers) {
            ids.Insert(0, StoryOwner.Of(EClass.pc.uid));
        }

        var list = card.ListArea(ids.Count, maxRows, TeamLineHeight);
        foreach (var team in ids) {
            var parts = new List<string>();
            if (TeamBills.On) {
                parts.Add("emp_ui_team_bills".Loc(TeamBills.UnpaidOf(team)));
            }
            if (TeamStory.On) {
                parts.Add("emp_ui_team_story".Loc(TeamStory.ChapterOf(team)));
            }

            var row = list.Row();
            row.LayoutElement().minHeight = row.LayoutElement().preferredHeight = TeamLineHeight;
            row.TextColumn(team < 0 ? Teams.PlayerName(-team) : Teams.NameOf(team), string.Join("  ·  ", parts));
        }
    }

    private const float TeamLineHeight = 40f;
}
