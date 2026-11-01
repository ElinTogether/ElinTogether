using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Play page of the hub (dev/PLAN_multiplayer_hub.md step 4), the same for every player: in a game, who it
///     is with and how to invite or leave; otherwise three cards (host, join, public servers), one click each. Holds
///     what the Lobby and Session tabs did (the lobby list, the depot, Start Server, Invite, Leave)
/// </summary>
internal class TabPlay : TabEmpBase
{
    public const string TabName = "emp_tab_play";

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        var pages = new List<(string, System.Action<YKVertical>)> { ("emp_ui_tab_play".lang(), BuildPlay) };
#if DEBUG
        // the bot tools of the debug build, out of the player's way (coordinator, 2026-10-28)
        if (EClass.core.IsGameStarted && NetSession.Instance.Transport is not ElinNetClient) {
            pages.Add(("Debug", page => BuildBotButtons(page.Card("Debug"))));
        }
#endif
        SubTabs("play", pages);
        // content from the top edge, no centred empty band above it
        if (GetComponent<VerticalLayoutGroup>() is { } root) {
            root.childAlignment = TextAnchor.UpperLeft;
            root.childForceExpandHeight = false;
            root.padding.top = 0;
        }
    }

    private void BuildPlay(YKVertical page)
    {
        if (EClass.core.IsGameStarted && NetSession.Instance.Transport != null) {
            BuildConnected(page);
        } else if (NetSession.Instance is { HasActiveConnection: true, Transport: ElinNetClient }) {
            // a guest at the title screen, already joined and waiting for the world: no host or join cards (a second
            // join from here would drop the first), only where it stands and the way out
            BuildJoining(page);
        } else if (!EClass.core.IsGameStarted && _choosing) {
            BuildSaveList(page);
        } else {
            // the journal's two pages, read top-down (user 2026-11-01): hosting first, then the ways to join (the
            // public server, a friend) on the left, the public games on the right, scrolled inside their card
            var (left, right) = page.Book();
            if (EClass.core.IsGameStarted) {
                BuildHost(left);
            } else {
                BuildHostGame(left);
            }

            var official = BuildOfficialCard(left);
            BuildJoin(left);
            BuildPublicServers(right, official);
        }
    }

    // ---------------------------------------------------------------- connected

    private static void BuildConnected(YKVertical page)
    {
        var net = NetSession.Instance;
        var players = net.CurrentPlayers.ToList();
        var host = players.FirstOrDefault(p => p.Index == 0)?.User.Name;
        var isHost = net.Transport is ElinNetHost;
        var title = isHost || string.IsNullOrEmpty(host) ? "emp_ui_play_connected".lang() : "emp_ui_play_world".Loc(host!);
        var card = page.Card(title);

        // the facts as the character sheet lists them: a small label, the value
        // (the title already says "Connected": a state line only when it is something else)
        if (net.IsAway) {
            card.KeyValue("emp_ui_kv_state".lang(), "emp_ui_play_reconnecting".lang());
        }
        // (the whole server, players on other maps included)
        card.KeyValue("emp_ui_kv_players".lang(), net.ServerPlayerCount.ToString());
        card.KeyValue("emp_ui_kv_host".lang(), host);
        if (EClass._zone is { } zone) {
            card.KeyValue("emp_ui_kv_where".lang(), zone.NameWithLevel);
        }

        var buttons = card.ButtonRow();
        buttons.RowButton("emp_ui_sv_invite".lang(), net.Lobby.InviteSteamOverlay);
        buttons.RowButton("emp_ui_sv_dc".lang(), DisconnectFromPanel);

        BuildRulesNote(page);
    }

    /// <summary>
    ///     A friend sees the rules of the host's world, read only, as the host last sent them (council 2026-10-23)
    /// </summary>
    private static void BuildRulesNote(YKVertical page)
    {
        var isGuest = NetSession.Instance.Transport is ElinNetClient;
        var lines = SettingsTable.RuleLines(SettingsTable.FromRules(NetSession.Instance.Rules));
        var card = page.Card("emp_ui_rules_title".lang());
        if (isGuest) {
            card.TextSmall("emp_ui_rules_note".lang(), FontColor.Gray);
        } else {
            // the host: a short summary, the full list being one click away
            card.TextSmall("emp_ui_rules_summary".Loc(lines.Count), FontColor.Gray);
            lines = lines.Take(4).ToList();
        }

        foreach (var (label, value) in lines) {
            card.KeyValue(label, value);
        }
        if (isGuest && SettingsTable.LastNotice is { } notice) {
            card.TextSmall(notice);
        }
        if (!isGuest) {
            card.ButtonRow().RowButton("emp_ui_rules_open".lang(),
                () => LayerElinTogether.Instance?.Window.SwitchContent("emp_tab_server"));
        }
    }

    /// <summary>
    ///     A small lozenge in the colour of the state (the mark the settings page uses for "changed")
    /// </summary>
    private static void StatusDot(YKLayout row, Color color)
    {
        var go = new GameObject("StatusDot", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(row.transform, false);
        var element = go.GetComponent<LayoutElement>();
        element.minWidth = element.preferredWidth = 18f;
        element.minHeight = element.preferredHeight = 18f;
        var dot = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        dot.transform.SetParent(go.transform, false);
        var image = dot.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        var rt = (RectTransform)dot.transform;
        rt.anchorMin = rt.anchorMax = new(0.5f, 0.5f);
        rt.sizeDelta = new(10f, 10f);
        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
    }

    private static void BuildJoining(YKVertical page)
    {
        var card = page.Card("emp_ui_play_join_title".lang());
        var host = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.Index == 0)?.User.Name;
        card.KeyValue("emp_ui_kv_state".lang(), "emp_ui_play_joining".lang());
        card.KeyValue("emp_ui_kv_host".lang(), host);
        card.ButtonRow().RowButton("emp_ui_sv_dc".lang(), DisconnectFromPanel);
    }

    private static void DisconnectFromPanel()
    {
        // (a guest still at the title screen has no game to leave: back to the title only from a game)
        var isClient = NetSession.Instance.Transport is ElinNetClient && EClass.core.IsGameStarted;

        // (a host whose guests would take the world over gives them a last save first)
        ElinNetHost.LeaveWithLastCopy(true, () => {
            NetSession.Instance.ResetSession();
            LayerElinTogether.Instance?.Reopen();

            if (isClient) {
                EMono.scene.Init(Scene.Mode.Title);
            }
        });
    }

    // ---------------------------------------------------------------- not connected

    // ---------------------------------------------------------------- host a game (title screen)

    // the title screen's "Host a game" (user 2026-11-01): a save is chosen here, the rules page shows it with
    // "Start hosting", which loads it and opens it to multiplayer (EmpAutoHost.HostAfterLoad). Never stored
    private static bool _choosing;

    /// <summary>
    ///     The save chosen to be hosted, shown on the rules page with its Start button until it starts or Back
    /// </summary>
    internal static GameIndex? HostPick { get; private set; }

    private static bool Cloud => EClass.core.config.cloud;

    /// <summary>
    ///     "Host a game": one button that lists the saves; the shared world of the depot, when there is one, as a
    ///     second line under it
    /// </summary>
    private static void BuildHostGame(YKVertical page)
    {
        var card = page.Card("emp_ui_host_game_title".lang());
        card.TextSmall("emp_ui_host_game_desc".lang(), FontColor.Gray);
        card.ButtonRow().RowButton("emp_ui_host_game_choose".lang(), () => {
            _choosing = true;
            LayerElinTogether.Instance?.Reopen();
        });

        if (!SaveDepot.Enabled) {
            return;
        }

        // the world kept in the depot: whoever comes first takes it and hosts it
        card.Divider();
        card.TextSmall("emp_ui_play_host_depot".lang(), FontColor.Gray);
        // held by another player whose lock says where: the same click joins their game
        var label = SaveDepot.HeldBy() is not { } who ? "emp_ui_depot_take".lang() :
            (SaveDepot.Joinable ? "emp_ui_depot_join_btn" : "emp_ui_depot_held_btn").Loc(who);
        card.ButtonRow().RowButton(label, () => {
            LayerElinTogether.Instance?.Close();
            SaveDepot.Take();
        });
    }

    /// <summary>
    ///     The player's saves as the game's Load window reads them (GameIO.GetGameList), the most recent first; the
    ///     depot's own copy of the shared world is hosted from its line, not from here
    /// </summary>
    private static List<GameIndex> Saves()
    {
        try {
            return GameIO.GetGameList(Cloud ? CorePath.RootSaveCloud : CorePath.RootSave)
                .Where(g => !g.IsCorrupted && !(SaveDepot.Enabled && g.id == SaveDepot.WorldId))
                .OrderByDescending(g => g.real?.GetRawReal() ?? 0)
                .ToList();
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "The saves could not be listed");
            return [];
        }
    }

    private static void BuildSaveList(YKVertical page)
    {
        var card = page.Card("emp_ui_host_game_choose".lang());
        var saves = Saves();
        card.TextSmall((saves.Count == 0 ? "emp_ui_host_pick_none" : "emp_ui_host_pick_desc").lang(), FontColor.Gray);
        if (saves.Count > 0) {
            var list = card.ScrollList(380f);
            list.Layout.spacing = UIHelper.RowSpacing * 2f;
            for (var i = 0; i < saves.Count; i++) {
                if (i > 0) {
                    list.Divider();
                }

                BuildSaveEntry(list, saves[i]);
            }
        }

        card.ButtonRow().RowButton("emp_ui_host_back".lang(), () => {
            _choosing = false;
            LayerElinTogether.Instance?.Reopen();
        });
    }

    /// <summary>
    ///     One save as the Load window writes it: its name, the character and where it is, the date it was last
    ///     played and the days played; a save of another version of the game cannot be chosen
    /// </summary>
    private static void BuildSaveEntry(YKLayout parent, GameIndex save)
    {
        var line = parent.Row();
        line.Layout.childAlignment = TextAnchor.UpperLeft;
        var text = line.Vertical();
        text.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        text.Layout.spacing = 0f;
        text.LayoutElement().flexibleWidth = 1f;
        text.LayoutElement().minWidth = 0f;

        var compatible = EClass.core.version.IsSaveCompatible(save.version);
        text.Text(SaveName(save), compatible ? FontColor.Default : FontColor.Bad);
        text.TextSmall($"{(save.permaDeath ? "★" : "")}{save.pcName} ({save.zoneName})", FontColor.Gray);
        text.TextSmall($"{save.RealDate}   {"infoHire".lang(save.days.ToString())}", FontColor.Gray);
        if (!compatible) {
            text.TextSmall("incompatible".lang(), FontColor.Bad);
            return;
        }

        var target = save;
        var pick = line.Button("emp_ui_host_pick".lang(), () => {
            HostPick = target;
            _choosing = false;
            // the rules page, on its presets: the Start button is at its top
            SetSubTab("server", 0);
            LayerElinTogether.Instance?.Close();
            LayerElinTogether.OpenPanelSesame("emp_tab_server");
        });
        var element = pick.LayoutElement();
        element.preferredWidth = 110f;
        element.flexibleWidth = 0f;
    }

    internal static string SaveName(GameIndex save) => string.IsNullOrEmpty(save.factionName) ? save.id : save.factionName;

    /// <summary>
    ///     Back from the rules page to the list of saves
    /// </summary>
    internal static void BackToSaves()
    {
        HostPick = null;
        _choosing = true;
        LayerElinTogether.Instance?.Close();
        LayerElinTogether.OpenPanelSesame(TabName);
    }

    /// <summary>
    ///     Loads the chosen save as the game's Load window does (a backup first for a save of an older version), and
    ///     asks for it to be opened to multiplayer once it is loaded (EmpAutoHost.OpenSession)
    /// </summary>
    internal static void StartHosting(GameIndex save)
    {
        HostPick = null;
        _choosing = false;
        var cloud = Cloud;
        save.cloud = cloud;
        LayerElinTogether.Instance?.Close();
        try {
            if (!save.madeBackup && save.version.IsBelow(EClass.core.version)) {
                GameIO.MakeBackup(save);
                EClass.ui.Say("backupDone");
                save.madeBackup = true;
                GameIO.UpdateGameIndex(save);
            }
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "No backup of the save {Id} before loading it", save.id);
        }

        EmpAutoHost.HostAfterLoad = save.id;
        LayerTitle.KillActor();
        // (an incompatible save: the game says so, and nothing is opened)
        if (!Game.TryLoad(save.id, cloud, () => Game.Load(save.id, cloud))) {
            EmpAutoHost.HostAfterLoad = null;
        }
    }

    private static void BuildHost(YKVertical page)
    {
        var card = page.Card("emp_ui_play_host_title".lang());
        card.TextSmall("emp_ui_play_host_desc".lang(), FontColor.Gray);
        var buttons = card.ButtonRow();
        buttons.RowButton("emp_ui_sv_start".lang(), StartServerFromPanel);
        if (SaveDepot.Enabled && Game.id != SaveDepot.WorldId) {
            buttons.RowButton("emp_ui_depot_put".lang(), SaveDepot.Put);
        }

        // where "Start Server" fails
        if (EClass.player.chara?.homeBranch?.owner is null) {
            card.TextSmall("emp_ui_unclaimed_zone".lang());
        }
    }

    private static void BuildJoin(YKVertical page)
    {
        var card = page.Card("emp_ui_play_join_title".lang());
        if (EClass.core.IsGameStarted) {
            // (joining leaves this game: from the title screen only, as before)
            card.TextSmall("emp_ui_play_join_ingame".lang(), FontColor.Gray);
            return;
        }

        card.TextSmall("emp_ui_play_join_desc".lang(), FontColor.Gray);
        // a server: a game nobody plays, started with -empserver, joined by its address
        card.ButtonRow().RowButton("emp_ui_join_address".lang(), () => {
            var last = EmpConfig.Client.ServerAddress;
            Dialog.InputName("emp_ui_join_address_ask", last.Value.Length > 0 ? last.Value : "127.0.0.1:55556", (cancel, text) => {
                if (cancel || text.Trim().Length == 0) {
                    return;
                }

                last.Value = text.Trim();
                LayerElinTogether.Instance?.Close();
                JoinAddress(last.Value);
            }).input.field.characterLimit = 0;
        });
    }

    /// <summary>
    ///     One address for both kinds of server: Elin Together Server without a game (its world is taken and
    ///     hosted here), or a game started with -empserver (joined)
    /// </summary>
    internal static void JoinAddress(string address)
    {
        if (!SaveDepot.TakeFrom(address)) {
            NetSession.Instance.InitializeComponent<ElinNetClient>().ConnectAddress(address);
        }
    }

#if DEBUG
    /// <summary>
    ///     A second game on this machine, which joins and plays by itself
    /// </summary>
    private static void BuildBotButtons(YKVertical card)
    {
        if (NetSession.Instance.Transport is ElinNetClient) {
            return;
        }

        var botGroup = card.ButtonRow();

        botGroup.RowButton("emp_ui_bot_add".lang(), () => {
            EmpBotLauncher.Launch();
            LayerElinTogether.Instance?.Reopen();
        });

        var running = EmpBotLauncher.Running;
        if (running > 0) {
            botGroup.RowButton("emp_ui_bot_stop".Loc(running), () => {
                EmpBotLauncher.StopAll();
                LayerElinTogether.Instance?.Reopen();
            });
        }

        card.Toggle("emp_ui_bot_all_actions", EmpConfig.Dev.BotAllActions.Value,
                value => EmpConfig.Dev.BotAllActions.Value = value).AsRow()
            .SetTooltipLang(EmpConfig.Dev.BotAllActions.Description.Description);
    }
#endif

    /// <summary>
    ///     The official server: its own card at the top, always there (also closed), with its state and Join. Filled
    ///     when the lobby list answers (BuildPublicServers)
    /// </summary>
    private static YKVertical BuildOfficialCard(YKVertical page)
    {
        var card = page.Card("emp_ui_official_title".lang());
        card.TextSmall("emp_ui_play_searching".lang(), FontColor.Gray);
        return card;
    }

    private static void FillOfficial(YKVertical card, LobbyData? lobby)
    {
        // (drop the "searching" line: the header is outside the body)
        for (var i = card.transform.childCount - 1; i >= 0; i--) {
            UnityEngine.Object.Destroy(card.transform.GetChild(i).gameObject);
        }

        if (lobby is not { } found) {
            card.TextSmall("emp_ui_official_closed".lang(), FontColor.Gray);
            return;
        }

        var line = card.Row();
        StatusDot(line, new(0.45f, 0.68f, 0.32f));
        line.Chip("emp_ui_lobby_official".lang(), FontColor.Good);
        line.Text("emp_ui_official_open".Loc(found.MemberCount, OfficialServer.MaxPlayers));
        card.TextSmall("emp_ui_lobby_public".lang(), FontColor.Gray);
        if (!EClass.core.IsGameStarted) {
            card.ButtonRow().RowButton("emp_ui_lobby_official_join".lang(), () => {
                LayerElinTogether.Instance?.Close();
                NetSession.Instance.Lobby.ConnectLobby(found);
            });
        }
    }

    private void BuildPublicServers(YKVertical page, YKVertical officialCard)
    {
        var card = page.Card("emp_ui_play_public_title".lang());
        var status = card.TextSmall("emp_ui_play_searching".lang(), FontColor.Gray);
        var list = card.ScrollList(380f);
        list.Layout.spacing = UIHelper.RowSpacing * 2f;

        NetSession.Instance.Lobby.GetOnlineLobbies(SetupLobbyDisplay);

        return;

        void SetupLobbyDisplay(LobbyData[] lobbies)
        {
            if (this == null) {
                return;
            }

            // the project's own public game first (user, 2026-10-27), checked by its signature, not by its name
            var all = lobbies.Select(l => (Lobby: l, Official: OfficialServer.IsOfficial(l))).ToArray();
            FillOfficial(officialCard, all.Where(l => l.Official).Select(l => (LobbyData?)l.Lobby).FirstOrDefault());
            var sorted = all.Where(l => !l.Official).ToArray();
            var total = sorted.Sum(l => l.Lobby.MemberCount);
            status.text = sorted.Length == 0 ? "emp_ui_play_none".lang() : "emp_ui_lobby_tally".Loc(total);
            if (sorted.Length == 0) {
                // (a next step, as on every empty page)
                list.ButtonRow().RowButton("emp_ui_play_refresh".lang(), () => LayerElinTogether.Instance?.Reopen());
            }

            for (var i = 0; i < sorted.Length; i++) {
                if (i > 0) {
                    list.Divider();
                }

                BuildLobbyEntry(list, sorted[i].Lobby, sorted[i].Official);
            }

            // (these lines come after the layer was laid out, see LayerElinTogether.OnLayout)
            UIHelper.WrapTexts(this);
            Relayout();
        }
    }

    /// <summary>
    ///     One game: its name (the official one with its badge), who may come in, what it runs with, and Join (from the
    ///     title screen: one click joins that game; in a game, the player leaves it first)
    /// </summary>
    private static void BuildLobbyEntry(YKLayout parent, LobbyData lobby, bool isOfficial)
    {
        // the game's words on the left, a Join as wide as its word on the right (a "Join <name>" bar under each game
        // made the list twice as long)
        var line = parent.Row();
        line.Layout.childAlignment = TextAnchor.UpperLeft;
        var game = line.Vertical();
        game.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        game.Layout.spacing = 0f;
        game.LayoutElement().flexibleWidth = 1f;
        game.LayoutElement().minWidth = 0f;
        if (isOfficial) {
            game.Chip("emp_ui_lobby_official".lang(), FontColor.Good);
        }

        game.Text("emp_ui_lobby_desc".Loc(lobby.Name, lobby.GameVersion, lobby.MemberCount, lobby[EmpLobbyData.CurrentZone]));
        // who may come in, said before trying (a lobby of an older version says nothing)
        if (isOfficial || lobby[EmpLobbyData.Public] == "1") {
            game.TextSmall("emp_ui_lobby_public".lang(), FontColor.Gray);
        } else if (lobby[EmpLobbyData.Public] == "0") {
            game.TextSmall("emp_ui_lobby_friends".lang(), FontColor.Gray);
        }

        // what that game runs with, when its host says so: known before joining
        if (ModList.Summary(lobby[EmpLobbyData.Mods]) is { } mods) {
            game.TextSmall(mods.Missing > 0
                ? "emp_ui_mods_lobby_missing".Loc(mods.Count, mods.Missing)
                : "emp_ui_mods_lobby".Loc(mods.Count), FontColor.Gray);
        }

        if (!EClass.core.IsGameStarted) {
            var target = lobby;
            var join = line.Button("emp_ui_team_join_short".lang(), () => {
                LayerElinTogether.Instance?.Close();
                NetSession.Instance.Lobby.ConnectLobby(target);
            });
            var element = join.LayoutElement();
            element.preferredWidth = 90f;
            element.flexibleWidth = 0f;
        }
    }

    private static void StartServerFromPanel()
    {
        // (with the box "hidden server", the world starts in a second, hidden game and this one joins it)
        if (HiddenServer.TryStart(() => NetSession.Instance.InitializeComponent<ElinNetHost>().StartServer())) {
            LayerElinTogether.Instance?.Close();
            return;
        }

        NetSession.Instance.InitializeComponent<ElinNetHost>().StartServer();
        LayerElinTogether.Instance?.Reopen();
    }
}
