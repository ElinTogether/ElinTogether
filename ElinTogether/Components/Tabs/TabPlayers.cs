using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Players page of the hub (dev/PLAN_multiplayer_hub.md step 4), the same for every player: a row per
///     player, one "..." menu with the same entries for everyone (admin entries only when permitted, as the Session
///     tab had them), then the folded host lists (saved characters, bans)
/// </summary>
internal class TabPlayers : TabEmpBase
{
    public const string TabName = "emp_tab_players";

    private static bool HostLists => NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false };

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        // sub-tabs: who is here, the letters (host box "Mailbox"), then for the host the characters of this world and the bans
        var pages = new List<(string, Action<YKVertical>)> {
            ("emp_ui_sub_online".lang(), BuildPlayerList),
            ("emp_ui_sub_mail".lang(), page => TabMail.Build(page, Relayout)),
        };
        if (HostLists) {
            pages.Add(("emp_ui_sub_characters".lang(), BuildSavedCharacters));
            pages.Add(("emp_ui_sub_banned".lang(), BuildBannedList));
        }

        SubTabs("players", pages);
    }

    internal const int MailSubTab = 1;

    protected override void OnSubTabShown(int index)
    {
        if (index == MailSubTab && Mailbox.On) {
            Mailbox.AskList();
        }
    }

    private void OnDestroy()
    {
        TabMail.Release(this);
    }

    // the player whose page is open on the right (by character), kept over reopenings
    private static int _picked;

    /// <summary>
    ///     The journal's two pages: the players on the left, the picked one's facts and actions on the right
    /// </summary>
    private static void BuildPlayerList(YKVertical page)
    {
        var (left, right) = page.Book();
        // search, sort and filter stay put (a typed text must keep its focus); only the list under them is rebuilt
        var controls = left.Vertical();
        controls.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        controls.Layout.spacing = UIHelper.RowSpacing;
        var list = left.Vertical();
        list.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        list.Layout.spacing = UIHelper.RowSpacing;

        var search = controls.InputText(_search);
        search.field.characterLimit = 40;
        search.StyleAsSearch("emp_ui_pl_search".lang());
        search.field.onValueChanged.AddListener(text => {
            _search = text.Trim();
            _page = 0;
            FillBook(list, right);
        });
        var bar = controls.ButtonRow();
        UIButton? sortButton = null;
        sortButton = bar.RowButton("emp_ui_pl_sort".Loc(SortName()), () => {
            _sort = (_sort + 1) % SortKeys.Length;
            _page = 0;
            sortButton!.mainText.SetText("emp_ui_pl_sort".Loc(SortName()));
            FillBook(list, right);
        });
        if (NetSession.Instance.Rules.UseTeams) {
            UIButton? teamButton = null;
            teamButton = bar.RowButton(TeamLabel(), () => {
                _myTeam = !_myTeam;
                _page = 0;
                teamButton!.mainText.SetText(TeamLabel());
                FillBook(list, right);
            });
        }

        FillBook(list, right);
    }

    private static readonly string[] SortKeys = ["name", "level", "ping", "team"];
    private static string _search = "";
    private static int _sort;
    private static bool _myTeam;
    private static int _page;
    private const int PageSize = 20;

    private static string SortName() => ("emp_ui_pl_sort_" + SortKeys[_sort]).lang();

    private static string TeamLabel() => (_myTeam ? "[x] " : "") + "emp_ui_pl_myteam".lang();

    /// <summary>
    ///     A line of the list: a player of the server's roster (ServerRoster, away ones included), with this map's state
    ///     and character when it plays here. Before any roster arrived (a host of an older version), this map's players
    /// </summary>
    private sealed class Row(RosterEntry? entry, NetPeerState? state, Chara? chara)
    {
        internal readonly RosterEntry? Entry = entry;
        internal readonly NetPeerState? State = state;

        // null: not on this map (the roster's facts only)
        internal readonly Chara? Chara = chara;

        internal int Uid => Entry?.CharaUid ?? State!.CharaUid;
        internal bool IsHost => Entry?.IsHost ?? State!.Index == 0;
        internal bool IsMe => EClass.pc is { } pc ? Uid == pc.uid : State?.User.IsMe ?? false;
        internal ulong Account => Entry?.Steam ?? (ulong)State!.User;

        // the host's peer id of that player (what the host's kick takes)
        internal int PeerIndex => Entry?.Peer ?? State!.Index;
        internal string CharaName => Chara?.Name ?? Entry?.CharaName ?? "?";
        internal int Level => Chara?.LV ?? Entry?.Level ?? 0;
        internal string Name => (Entry?.Name ?? State?.User.Name) is { } n && !string.IsNullOrWhiteSpace(n) ? n : CharaName;
        internal float Ping => State is { } s ? s.AvgPingMs > 0 ? s.AvgPingMs : s.LastPingMs : Entry?.PingMs ?? 0;
        internal bool Idle => State?.Idle ?? Entry?.Idle ?? false;
        internal string? Where => Chara?.currentZone?.NameWithLevel ?? Entry?.Where;
        // (this game's team list, else the host's word for a player it knows less of)
        internal int Team => Teams.TeamOf(Uid) is var t && (t != Teams.Wanderers || Entry is null) ? t : Entry.Team;

        // a picture only: a far player's copy in this world may be old
        internal Chara? Picture => Chara ?? EClass.game?.cards.globalCharas.Find(Uid);
    }

    private static List<Row> AllRows()
    {
        var session = NetSession.Instance;
        var here = session.VisiblePlayers.ToList();
        if (session.ServerRoster.Count == 0) {
            return here.Select(p => new Row(null, p, p.FindChara())).ToList();
        }

        var rows = new List<Row>();
        foreach (var entry in session.ServerRoster) {
            var state = here.FirstOrDefault(p => p.CharaUid == entry.CharaUid);
            var chara = state?.FindChara();
            rows.Add(new(entry, chara is null ? null : state, chara));
        }

        // (one's own line before the host's next roster has it)
        if (EClass.pc is { } pc && !rows.Exists(r => r.Uid == pc.uid) && here.FirstOrDefault(p => p.CharaUid == pc.uid) is { } self) {
            rows.Add(new(null, self, self.FindChara()));
        }

        return rows;
    }

    private static List<Row> Visible(List<Row> all)
    {
        IEnumerable<Row> q = all;
        if (_search.Length > 0) {
            q = q.Where(p => p.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.CharaName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        if (_myTeam && EClass.pc is { } pc) {
            var mine = Teams.TeamOf(pc.uid);
            q = q.Where(p => p.Team == mine);
        }

        // (you first, then the host, whatever the order)
        var ordered = q.OrderBy(p => p.IsMe ? 0 : p.IsHost ? 1 : 2);
        return (SortKeys[_sort] switch {
            "level" => ordered.ThenByDescending(p => p.Level),
            "ping" => ordered.ThenBy(p => p.Ping),
            "team" => ordered.ThenBy(p => Teams.NameOf(p.Team)),
            _ => ordered,
        }).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void FillBook(YKVertical left, YKVertical right)
    {
        foreach (Transform child in left.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
        foreach (Transform child in right.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var all = AllRows();
        var players = Visible(all);
        var pages = Mathf.Max(1, (players.Count + PageSize - 1) / PageSize);
        _page = Mathf.Clamp(_page, 0, pages - 1);
        var picked = all.FirstOrDefault(p => p.Uid == _picked) ?? all.FirstOrDefault(p => p.IsMe) ?? all.FirstOrDefault();
        var card = left.Card("emp_ui_players_title".Loc(players.Count));
        // (five rows in the page under the search and the sort: more scroll inside the card)
        var shown = players.Skip(_page * PageSize).Take(PageSize).ToList();
        var rows = card.ListArea(shown.Count, 5, 60f);
        foreach (var player in shown) {
            var target = player;
            BuildPlayerLine(rows, player, player == picked, () => {
                _picked = target.Uid;
                FillBook(left, right);
            });
        }

        if (players.Count == 0 && all.Count > 0) {
            card.EmptyText("emp_ui_pl_none".lang());
        }
        if (pages > 1) {
            var nav = card.ButtonRow();
            nav.Layout.childAlignment = TextAnchor.MiddleCenter;
            nav.SignButton("<", () => {
                _page = (_page + pages - 1) % pages;
                FillBook(left, right);
            }, 44f);
            nav.Text("emp_ui_pl_page".Loc(_page + 1, pages));
            nav.SignButton(">", () => {
                _page = (_page + 1) % pages;
                FillBook(left, right);
            }, 44f);
        }

        if (picked is not null) {
            BuildPlayerPage(right, picked);
        }

        // nobody else: say so, and the one click that helps (invite when in a game, else the Play page)
        if (all.Count <= 1) {
            if (NetSession.Instance.InGame) {
                card.EmptyState("emp_ui_players_alone".lang(), "emp_ui_players_alone_btn".lang(),
                    NetSession.Instance.Lobby.InviteSteamOverlay);
            } else {
                card.EmptyState("emp_ui_players_alone".lang(), "emp_ui_players_go_play".lang(),
                    () => LayerElinTogether.Instance?.Window.SwitchContent(TabPlay.TabName));
            }
        }

        UIHelper.WrapTexts(left);
        UIHelper.WrapTexts(right);
        left.GetComponentInParent<TabEmpBase>()?.Relayout();
    }

    /// <summary>
    ///     The right page: the player's name, the facts of the character sheet (character, level, team, ping, role),
    ///     then what can be done, one button each (the entries the "..." menu had). A player on another map: the facts
    ///     the host's roster gives
    /// </summary>
    private static void BuildPlayerPage(YKVertical page, Row player)
    {
        var chara = player.Chara;
        var name = player.Name;

        // the character sheet's head: portrait and sprite on the left with some air, then the name as a title (in full,
        // no header bullet over the portrait) and the facts under it
        var head = page.Row();
        head.Layout.padding = new(12, 8, 4, 12);
        head.Layout.spacing = 14f;
        head.Layout.childAlignment = TextAnchor.UpperLeft;
        // (a row fits its MIN height by default, 32: the row must take its preferred one, the taller of portrait and
        // facts, so Actions starts under the whole head)
        head.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        if (player.Picture is { } picture) {
            head.Portrait(picture);
        }
        var lines = head.Vertical();
        // (the game's TopicDefault lines report no preferred height: a column that controls heights squashes them to
        // zero, all drawn on one line; left alone they keep the prefab's 22 and the column sums those)
        lines.Layout.childControlHeight = false;
        lines.Layout.childForceExpandHeight = false;
        lines.LayoutElement().flexibleWidth = 1f;
        lines.Layout.spacing = 2f;
        lines.Text(name).SetSize(UIHelper.LabelSize + 4);
        if (chara?.GetStr(PlayerTitleDelta.TitleKey) is { Length: > 0 } title) {
            lines.KeyValue("emp_ui_kv_title".lang(), title);
        }

        lines.KeyValue("emp_ui_kv_character".lang(), player.CharaName);
        lines.KeyValue("emp_ui_kv_level".lang(), player.Level.ToString());
        if (chara is not null) {
            lines.KeyValue("emp_ui_kv_race_job".lang(), $"{chara.race.GetText()}, {chara.job.GetText()}");
        }
        if (NetSession.Instance.Rules.UseTeams) {
            var teamId = player.Team;
            if (TeamBanner.On && teamId != Teams.Wanderers && TeamBanner.EmblemSprite(teamId) is { } emblem) {
                var teamRow = lines.Row();
                teamRow.Layout.spacing = 6f;
                EmblemZoom.Zoomable(TeamEmblemBaker.Place(teamRow.transform, emblem, 28f), teamId);
                teamRow.Text("emp_ui_kv_team".lang() + ": " + Teams.NameOf(teamId));
            } else {
                lines.KeyValue("emp_ui_kv_team".lang(), Teams.NameOf(teamId));
            }
        }
        lines.KeyValue("emp_ui_kv_where".lang(), player.Where);
        // (no ping for yourself, nor for the host's own line)
        if (!player.IsHost && !player.IsMe) {
            var ms = player.Ping;
            lines.KeyValue("emp_ui_kv_ping".lang(), ms <= 0 ? null : $"{Mathf.RoundToInt(ms)} ms");
        }
        lines.KeyValue("emp_ui_kv_role".lang(), RoleOf(player));
        lines.KeyValue("emp_ui_kv_session".lang(), SessionOf(player));
        if (chara is not null) {
            AddRecords(lines, chara);
            AddDeeds(page, chara);
        }

        var actions = ActionsFor(player);
        if (actions.Count == 0) {
            return;
        }

        // as wide as their labels, two to a line where they fit (one full-width bar each ran off the page)
        page.Card("emp_ui_pl_actions".lang()).ButtonFlow(actions);
    }

    // when this game first saw each account in the session (honest: not the host's join time, nothing is sent)
    private static readonly Dictionary<ulong, float> _seen = [];
    private static Game? _seenGame;

    private static string SessionOf(Row player)
    {
        if (!ReferenceEquals(_seenGame, EClass.game)) {
            _seen.Clear();
            _seenGame = EClass.game;
        }

        var key = player.Account;
        if (!_seen.TryGetValue(key, out var since)) {
            _seen[key] = since = Time.realtimeSinceStartup;
        }

        var minutes = Mathf.FloorToInt((Time.realtimeSinceStartup - since) / 60f);
        return minutes >= 60 ? "emp_ui_time_hm".Loc(minutes / 60, minutes % 60) : "emp_ui_time_m".Loc(minutes);
    }

    /// <summary>
    ///     Deepest floor and duels won, as the host's boards tell (only the first ten of each board are sent, so a player
    ///     outside them shows nothing). Needs the Leaderboards box
    /// </summary>
    private static void AddRecords(YKVertical lines, Chara chara)
    {
        if (!Leaderboards.On) {
            return;
        }

        foreach (var (board, label) in new[] { (Leaderboards.Deepest, "emp_ui_kv_deepest"), (Leaderboards.Duels, "emp_ui_kv_duels") }) {
            if (Leaderboards.Of(board) is null) {
                Leaderboards.Ask(board);
            }

            if (Leaderboards.Of(board) is { } answer && Array.IndexOf(answer.Uids, chara.uid) is >= 0 and var at) {
                lines.KeyValue(label.lang(), answer.Scores[at].ToString());
            }
        }
    }

    /// <summary>
    ///     The last three lines of the activity journal done by that player (the ones this viewer may read)
    /// </summary>
    private static void AddDeeds(YKVertical page, Chara chara)
    {
        if (!ActivityJournal.On || EClass.pc is not { } pc) {
            return;
        }

        var deeds = ActivityJournal.Entries().Where(e => e.Actor == chara.uid && ActivityJournal.Visible(e, pc.uid))
            .Reverse().Take(3).ToList();
        if (deeds.Count == 0) {
            return;
        }

        var card = page.Card("emp_ui_pl_deeds".lang());
        foreach (var deed in deeds) {
            card.TextSmall(ActivityJournal.Line(deed), FontColor.Gray);
        }
    }

    private static string RoleOf(Row player)
    {
        var role = player.IsHost ? "emp_ui_player_host".lang()
            : ModerationDelta.AdminIds.Contains(player.Account) ? "emp_ui_role_admin".lang()
            : ModerationDelta.ModeratorIds.Contains(player.Account) ? "emp_ui_role_mod".lang() : "emp_ui_role_player".lang();
        if (ModerationDelta.FrozenIds.Contains(player.Account)) {
            role += ", " + "emp_ui_role_frozen".lang();
        }
        if (player.Idle) {
            role += ", " + "emp_ui_role_idle".lang();
        }
        return role;
    }

    /// <summary>
    ///     The host: the characters players made in this world, each with Delete when nobody plays it (upstream
    ///     1f932a8: a character left by someone who will not come back stayed in the world for good)
    /// </summary>
    private static void BuildSavedCharacters(YKVertical page)
    {
        var body = page.Card("emp_ui_saved_chars_title".lang());
        body.TextSmall("emp_ui_saved_chars_desc".lang(), FontColor.Gray);
        if (NetSession.Instance.Transport is not ElinNetHost host || host.SavedCharacters() is not { Count: > 0 } saved) {
            body.EmptyText("emp_ui_saved_chars_none".lang());
            return;
        }

        var list = body.ListArea(saved.Count, 8, 40f);
        foreach (var (user, chara, playing) in saved.OrderBy(s => s.User)) {
            // (a Steam account this game knows no name for showed as its raw number: the character alone then)
            var account = UserData.Get(user).Name is { Length: > 0 } name ? name : null;
            var row = list.Row();
            row.Portrait(chara, 36f);
            var line = account is null ? "emp_ui_saved_chars_line_alone".Loc(chara.Name, chara.LV)
                : "emp_ui_saved_chars_line".Loc(account, chara.Name, chara.LV);
            row.TextColumn(line + (chara.isDead ? " †" : ""), playing ? "emp_ui_saved_chars_playing".lang() : null);
            if (playing) {
                continue;
            }

            var target = chara;
            // the game's trash icon, its word as the hover text
            row.IconButton(EmpUi.GameIcons?.trash, "x", () => {
                Dialog.YesNo("emp_ui_saved_chars_confirm".Loc(target.Name, account ?? "?"), () => {
                    host.HostDeletes(user, target.uid);
                    LayerElinTogether.Instance?.Reopen();
                });
            }, "emp_ui_saved_chars_delete");
        }
    }

    /// <summary>
    ///     The host: who is banned from this world, each with Unban (admin tools, council 2026-10-27)
    /// </summary>
    private static void BuildBannedList(YKVertical page)
    {
        var body = page.Card("emp_ui_banned_title".lang());
        if (ElinNetHost.Bans.Count == 0) {
            body.EmptyText("emp_ui_banned_none".lang());
            return;
        }

        var bans = ElinNetHost.Bans.ToList();
        var list = body.ListArea(bans.Count, 10, UIHelper.RowHeight);
        foreach (var (user, why) in bans) {
            var row = list.Row();
            row.Text(why.Length > 0 ? $"{user}  ({why})" : user.ToString()).LayoutElement().flexibleWidth = 1f;
            row.IconButton(EmpUi.GameIcons?.trash, "x", () => {
                ElinNetHost.Unban(user);
                LayerElinTogether.Instance?.Reopen();
            }, "emp_ui_unban");
        }
    }

    /// <summary>
    ///     One row per player (council 2026-10-26): portrait, name, character and level, where it is, team, ping, badges,
    ///     and the "..." menu
    /// </summary>
    private static void BuildPlayerLine(YKLayout parent, Row player, bool picked, Action onPick)
    {
        // (a player whose Steam name is empty is shown by the name of the character)
        var name = player.Name;
        if (player.IsMe) {
            name += "  " + "emp_ui_player_you".lang();
        }

        // (one cut line: the character's name only when it is not the row's name already)
        var detail = player.CharaName == player.Name
            ? "emp_ui_kv_level".lang() + " " + player.Level
            : "emp_ui_player_detail".Loc(player.CharaName, player.Level);
        if (player.IsHost) {
            detail = "emp_ui_player_host".lang() + "  " + detail;
        }
        // where each one is: the server's players travel alone a lot (user 2026-10-30); first, as a long line is cut
        if (player.Where is { Length: > 0 } where && where != EClass._zone?.NameWithLevel) {
            detail = where + "  " + detail;
        }

        var badges = PlayerBadge.None;
        // away from the keyboard: time no longer waits for it (council 2026-10-26), said here only, no popup
        if (player.Idle) {
            badges |= PlayerBadge.Idle;
        }
        if (ModerationDelta.AdminIds.Contains(player.Account)) {
            badges |= PlayerBadge.Admin;
        } else if (ModerationDelta.ModeratorIds.Contains(player.Account)) {
            badges |= PlayerBadge.Moderator;
        }
        if (ModerationDelta.FrozenIds.Contains(player.Account)) {
            badges |= PlayerBadge.Frozen;
        }

        // (the host's own line has no ping)
        var ping = player.IsHost || player.IsMe ? -1 : EmpUi.PingBarsFor(player.Ping);
        var teamId = player.Team;
        var inTeam = NetSession.Instance.Rules.UseTeams && teamId != Teams.Wanderers;
        var team = inTeam ? Teams.NameOf(teamId) : null;
        parent.PickRow(new(name, detail, player.Picture, team, ping, badges, inTeam ? TeamBanner.ChipColor(teamId) : null,
            inTeam ? TeamBanner.EmblemSprite(teamId) : null), picked, onPick);
    }

    /// <summary>
    ///     The "..." entries of a player's row: the same for everyone, the admin ones only when permitted. A player on
    ///     another map: what goes through the host (a private line, a letter, the personal lists, moderation)
    /// </summary>
    private static List<(string Label, Action Act)> ActionsFor(Row player)
    {
        var actions = new List<(string, Action)>();
        var chara = player.Chara;
        var away = NetSession.Instance.IsAway;
        var isHost = NetSession.Instance.Transport is ElinNetHost;
        // Reconnect: a guest's own line only, or the host on a guest's line
        // (IsHost also reads true on an away client, and reconnecting would drop its zone)
        if (!away && player.State is { } state && (isHost ^ player.IsMe)) {
            actions.Add(("emp_ui_reconnect".lang(), () => ReconnectPlayer(state.Index)));
        }

        // what a player may do to another, with the host's switches (the same entries as in the game's own menu)
        if (!player.IsMe) {
            var other = player.Account;
            var otherName = player.Name;
            var uid = player.Uid;
            var here = !away && chara is { IsAliveInCurrentZone: true };
            if (PlayerWhisper.Enabled) {
                actions.Add(("emp_ui_act_whisper".lang(), () => PlayerWhisper.Open(uid, otherName)));
            }
            if (PlayerTrade.Enabled && here) {
                actions.Add(("emp_act_trade".lang(), () => PlayerTrade.Invite(uid)));
            }
            if (PlayerDuel.Enabled && here && !PlayerDuel.InDuel) {
                actions.Add(("emp_act_duel".lang(), () => PlayerDuel.Challenge(uid)));
            }
            // a letter, also kept for a player who leaves before reading it (host box "Mailbox")
            if (Mailbox.On) {
                actions.Add(("emp_ui_mail_write_to".lang(), () => {
                    TabMail.WriteTo(uid);
                    SetSubTab("players", MailSubTab);
                    LayerElinTogether.Instance?.Reopen();
                }));
            }

            actions.Add(((PlayerSocial.IsFriend(other) ? "emp_ui_unfriend" : "emp_ui_friend").lang(), () => {
                PlayerSocial.SetFriend(other, otherName, !PlayerSocial.IsFriend(other));
                LayerElinTogether.Instance?.Reopen();
            }));
            actions.Add(((PlayerSocial.IsIgnored(other) ? "emp_ui_unignore" : "emp_ui_ignore").lang(), () => {
                PlayerSocial.SetIgnored(other, otherName, !PlayerSocial.IsIgnored(other));
                LayerElinTogether.Instance?.Reopen();
            }));

            // a leader's invitation, as the Team page offers it (the host checks the same again)
            if (!away && EClass.pc is { } pc && Teams.LedBy(pc.uid) != Teams.Wanderers && Teams.TeamOf(uid) != Teams.TeamOf(pc.uid) &&
                Teams.InviteOf(uid) == Teams.Wanderers) {
                actions.Add(("emp_ui_team_invite".Loc(Teams.PlayerName(uid)), () => Teams.Ask(TeamAsk.Invite, uid)));
            }
        }

        // the moderation tools folded under one "Admin" button (UX audit 2026-11-01): a new player sees the few
        // actions meant for everyone
        if (!away) {
            var admin = new List<(string, Action)>();
            AddAdminActions(admin, player);
            if (admin.Count > 0) {
                actions.Add(("emp_ui_admin_menu".lang(), () => ShowMenu(admin)));
            }
        }

        return actions;
    }

    private static void ShowMenu(List<(string Label, Action Act)> entries)
    {
        var menu = EClass.ui.CreateContextMenuInteraction();
        foreach (var (label, act) in entries) {
            var action = act;
            menu.AddButton(label, () => action());
        }

        menu.Show();
    }

    /// <summary>
    ///     Moderation: the host and the admins, on the line of a player who is neither the host nor an admin
    /// </summary>
    private static void AddAdminActions(List<(string, Action)> actions, Row player)
    {
        var isHost = NetSession.Instance.Transport is ElinNetHost;
        var target = player.Account;
        var self = NetSession.Instance.Self;
        ulong me = self?.User ?? UserData.Me;
        var mine = player.IsMe;
        var admin = PeerHosting.IAmAdmin();
        var extras = isHost ? ElinNetHost.AdminExtrasOn : NetSession.Instance.Rules.AdminExtras;
        var moderator = !admin && extras && ModerationDelta.ModeratorIds.Contains(me);
        var peerIndex = player.PeerIndex;
        // the peer-hosted world: this player may no longer keep it (the world service's ban, an admin key on this PC)
        if (!mine && target != 0 && WorldAdmin.Shown) {
            var name = player.Name;
            actions.Add(("emp_wa_ban_keeping".lang(), () => TabWorldAdmin.AskBan(["steam:" + target], name)));
        }

        if (mine && admin) {
            if (extras) {
                actions.Add(("emp_ui_set_jail".lang(), () => Order("setjail", 0, peerIndex)));
            }
            if (ContainerLog.On) {
                actions.Add(("emp_ui_container_logs".lang(), () => ContainerLog.Ask(0)));
            }
        }

        if (player.IsHost || mine || ModerationDelta.AdminIds.Contains(target) || !(admin || moderator) ||
            (moderator && ModerationDelta.ModeratorIds.Contains(target))) {
            return;
        }

        actions.Add(("emp_ui_kick".lang(), () => Order("kick", target, peerIndex)));
        actions.Add(((ModerationDelta.IsMuted(target) ? "emp_ui_unmute" : "emp_ui_mute").lang(),
            () => Order(ModerationDelta.IsMuted(target) ? "unmute" : "mute", target, peerIndex)));
        if (!admin) {
            return;
        }

        actions.Add(("emp_ui_ban".lang(), () => Order("ban", target, peerIndex)));
        if (isHost) {
            actions.Add(("emp_ui_make_admin".lang(), () => {
                (NetSession.Instance.Transport as ElinNetHost)?.MakeAdmin(target, true);
                LayerElinTogether.Instance?.Reopen();
            }));
        }

        if (!extras) {
            return;
        }

        var held = ModerationDelta.FrozenIds.Contains(target);
        actions.Add(((held ? "emp_ui_unfreeze" : "emp_ui_freeze").lang(), () => Order(held ? "unfreeze" : "freeze", target, peerIndex)));
        actions.Add(("emp_ui_jail".lang(), () => Order("jail", target, peerIndex)));
        if (isHost) {
            var mod = ModerationDelta.ModeratorIds.Contains(target);
            actions.Add(((mod ? "emp_ui_unmake_mod" : "emp_ui_make_mod").lang(), () => {
                (NetSession.Instance.Transport as ElinNetHost)?.MakeModerator(target, !mod);
                LayerElinTogether.Instance?.Reopen();
            }));
        }
    }

    /// <summary>
    ///     The host does it; an admin asks the host, which checks it (AdminCommandDelta)
    /// </summary>
    private static void Order(string verb, ulong target, int peerIndex)
    {
        switch (NetSession.Instance.Transport) {
            case ElinNetHost host:
                switch (verb) {
                    case "kick":
                        host.Kick(peerIndex);
                        break;
                    case "ban":
                        host.Ban(target);
                        break;
                    case "mute":
                        host.Mute(target);
                        break;
                    case "unmute":
                        host.Unmute(target);
                        break;
                    case "freeze":
                        host.Freeze(target);
                        break;
                    case "jail":
                        host.Freeze(target, true);
                        break;
                    case "unfreeze":
                        host.Release(target);
                        break;
                    case "setjail":
                        host.SetJail(EClass._zone);
                        break;
                }

                break;
            case ElinNetClient client:
                client.Delta.AddRemote(new AdminCommandDelta { Verb = verb, Target = target });
                break;
        }

        LayerElinTogether.Instance?.Reopen();
    }

    private static void ReconnectPlayer(int peerIndex)
    {
        switch (NetSession.Instance.Transport) {
            case ElinNetHost host:
                host.RequestClientReconnect(peerIndex);
                break;
            case ElinNetClient client:
                client.ReconnectSelf();
                break;
        }
    }
}
