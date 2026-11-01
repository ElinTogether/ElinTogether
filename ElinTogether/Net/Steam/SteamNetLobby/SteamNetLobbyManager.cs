using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Steam;
using ElinTogether.LangMod;
using HeathenEngineering.SteamworksIntegration;
using HeathenEngineering.SteamworksIntegration.API;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net.Steam;

public class SteamNetLobbyManager : EClass
{
    private const string ConnectLobbyArg = "+connect_lobby";

    // on the lobby of a session opened by itself, that Steam lists: the browser only shows it to friends of its owner
    private const string HiddenKey = "emp_hidden";

    private readonly HashSet<UserData> _blocked = [];
    private readonly HashSet<UserData> _invited = [];

    public LobbyData Current;
    private Action<LobbyData[]>? _deferOnComplete;

    // the world the list under way was asked for, null for every game
    private string? _listWorld;

    /// <summary>
    ///     A list of games was asked and has not come yet
    /// </summary>
    internal bool IsListing => _deferOnComplete is not null;
    private bool _shutdown;
    private bool _quiet;
    private bool _invisible;

    /// <summary>
    ///     Asked by the host of a session opened by itself (ElinNetHost.StartServer sets it, and only then): is this
    ///     Steam account one of this world (it has, or had, a character in it)? Such a player comes in without being a
    ///     friend of the host. Null: only friends and invited players come in, as a "friends only" lobby does
    /// </summary>
    internal Func<ulong, bool>? KnownAccount;

    internal SteamNetLobbyManager()
    {
        RegisterCallbacks();
    }

    internal void Reset()
    {
        if (!_shutdown) {
            return;
        }

        _shutdown = false;
        RegisterCallbacks();
        EmpLog.Debug("Lobby manager reset, callbacks re-registered");
    }

    private void RegisterCallbacks()
    {
        SteamCallback<LobbyCreated_t>.Add(OnLobbyCreated);
        SteamCallback<LobbyChatUpdate_t>.Add(OnLobbyChatUpdate);
        SteamCallback<LobbyDataUpdate_t>.Add(OnLobbyDataUpdate);
        SteamCallback<GameLobbyJoinRequested_t>.Add(OnLobbyJoinRequested);
        SteamCallback<GameRichPresenceJoinRequested_t>.Add(OnRichPresenceJoinRequested);
        SteamCallback<LobbyEnter_t>.Add(OnLobbyEntered);
        SteamCallback<LobbyMatchList_t>.Add(OnLobbyMatchListComplete);
    }

    /// <summary>
    ///     Unregister all Steam callbacks
    /// </summary>
    internal void Shutdown()
    {
        if (_shutdown) {
            return;
        }

        _shutdown = true;

        LeaveLobby();

        SteamCallback<LobbyCreated_t>.Remove(OnLobbyCreated);
        SteamCallback<LobbyChatUpdate_t>.Remove(OnLobbyChatUpdate);
        SteamCallback<LobbyDataUpdate_t>.Remove(OnLobbyDataUpdate);
        SteamCallback<GameLobbyJoinRequested_t>.Remove(OnLobbyJoinRequested);
        SteamCallback<GameRichPresenceJoinRequested_t>.Remove(OnRichPresenceJoinRequested);
        SteamCallback<LobbyEnter_t>.Remove(OnLobbyEntered);
        SteamCallback<LobbyMatchList_t>.Remove(OnLobbyMatchListComplete);

        _deferOnComplete = null;
    }

    /// <summary>
    ///     Create a new lobby. We do this automatically on Host
    /// </summary>
    public void CreateLobby(SteamNetLobbyType type = SteamNetLobbyType.Public, int maxPlayers = 16, bool quiet = false)
    {
        LeaveLobby();

        // the game that plays on its hidden server has no lobby of its own: the server's is the only one of this
        // account (two lobbies of one player were two entries in everyone's list, the wrong one not joinable, live
        // report 2026-11-01)
        if (PlaysOnHiddenServer) {
            EmpLog.Warning("No lobby created: this game plays on its hidden server, whose lobby is the one friends join");
            return;
        }

        _quiet = quiet;
        _creating = true;

        EmpLog.Information("Creating steam {LobbyType} lobby",
            type);

        var lobbyType = type switch {
            SteamNetLobbyType.Public => ELobbyType.k_ELobbyTypePublic,
            SteamNetLobbyType.Friend => ELobbyType.k_ELobbyTypeFriendsOnly,
            // we use public to be able to search in list
            // though we do not join from here
            SteamNetLobbyType.Invite => ELobbyType.k_ELobbyTypePrivateUnique,
            _ => throw new ArgumentOutOfRangeException(nameof(SteamNetLobbyType), type, null),
        };

        // "friends only" cannot be joined by a player who is not a friend of the host, even with the lobby id (the
        // Steam server refuses it): a session opened by itself that must let the players of its world in is Public,
        // so that friends still join it from Steam, and marked hidden: the browser only shows it to friends of its
        // owner. Who really comes in is decided here, in OnLobbyChatUpdate
        _invisible = quiet && type == SteamNetLobbyType.Friend && KnownAccount is not null;
        if (_invisible) {
            lobbyType = ELobbyType.k_ELobbyTypePublic;
        }

#if DEBUG
        lobbyType = ELobbyType.k_ELobbyTypePrivateUnique;
#endif

        if (Signs || PublicServerCertificate.Issuer?.Keeps == true) {
            maxPlayers = Math.Max(maxPlayers, OfficialServer.MaxPlayers);
        }

        SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
    }

    /// <summary>
    ///     Leave current lobby if it's valid
    /// </summary>
    public void LeaveLobby()
    {
        Current.Leave();
        Current = CSteamID.Nil;
        _creating = false;
        // (one Steam account, two games: the rich presence is the account's, and the hidden server's says where to
        // join; the game that plays on it does not wipe it)
        if (!PlaysOnHiddenServer) {
            SteamFriends.ClearRichPresence();
        }
    }

    // a lobby asked of Steam and not created yet: the answer that comes when nothing waits for it any more is left
    private bool _creating;

    // the lobby this game asked to enter, see OnLobbyEntered
    private ulong _joining;

    /// <summary>
    ///     This game started a hidden server on this machine and plays on it (or is starting it): the server, on the
    ///     same Steam account, holds the only lobby
    /// </summary>
    private static bool PlaysOnHiddenServer => !EmpServer.Requested && (HiddenServer.Playing || HiddenServer.Starting);

    /// <summary>
    ///     Connect by lobby id
    /// </summary>
    public void ConnectLobby(LobbyData lobby)
    {
        if (Current == lobby && NetSession.Instance.HasActiveConnection) {
            EmpLog.Information("Ignoring join request for the already joined lobby {LobbyId}",
                lobby);

            EmpPop.Information("emp_lobby_already_joined".lang());
            return;
        }

        if (NetSession.Instance.HasActiveConnection) {
            NetSession.Instance.ResetSession();
        }

        LeaveLobby();

        if (core.IsGameStarted) {
            EMono.scene.Init(Scene.Mode.Title);
        }

        ELayerCleanup.Cleanup<LayerHelp>();

        if (NetSession.Instance.Transport is not ElinNetClient) {
            NetSession.Instance.InitializeComponent<ElinNetClient>();
        }

        (NetSession.Instance.Transport as ElinNetClient)?.MarkLobbyJoinRequested();
        _joining = lobby.SteamId.m_SteamID;
        SteamMatchmaking.JoinLobby(lobby);
    }

    /// <summary>
    ///     Invite by steam user id
    /// </summary>
    public void InviteSteamUser(UserData user)
    {
        _invited.Add(user);
        _blocked.Remove(user);
        Current.InviteUserToLobby(user);
    }

    /// <summary>
    ///     Invite by opening up overlay, requires launching from steam
    /// </summary>
    public void InviteSteamOverlay()
    {
        // (the game that plays on a hidden server is in no lobby: the server's is the one that invites)
        var hidden = HiddenServer.Playing ? HiddenServer.ServerLobby() : 0UL;
        SteamFriends.ActivateGameOverlayInviteDialog(hidden != 0 ? new CSteamID(hidden) : (CSteamID)Current);
    }

    /// <summary>
    ///     Fetch all current online lobbies
    /// </summary>
    /// <param name="world">only the games that host that world (EmpLobbyData.World), the hidden ones included</param>
    public void GetOnlineLobbies(Action<LobbyData[]> onComplete, string? world = null)
    {
        _deferOnComplete = onComplete;
        _listWorld = world;
        if (world is not null) {
            SteamMatchmaking.AddRequestLobbyListStringFilter(EmpLobbyData.World, world, ELobbyComparison.k_ELobbyComparisonEqual);
        }

#if !DEBUG
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            EmpLobbyData.EmpVersion, ModInfo.BuildVersion, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            EmpLobbyData.GameBuild, BuildVersionIntegrity.GameVersion, ELobbyComparison.k_ELobbyComparisonEqual);
#endif
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.RequestLobbyList();
    }

    /// <summary>
    ///     Update SteamFriends grouping
    /// </summary>
    public void UpdateRichPresence()
    {
        var sessionId = NetSession.Instance.SessionId;
        if (sessionId == 0) {
            return;
        }

        // assign friend grouping
        var sessionKey = sessionId.ToString();
        SteamFriends.SetRichPresence("steam_player_group", sessionKey);
        SteamFriends.SetRichPresence("steam_player_group_size", Current.MemberCount.ToString());
        SteamFriends.SetRichPresence("connect", $"{ConnectLobbyArg} {sessionKey}");
    }

    /// <summary>
    ///     Parse from steam launch args
    /// </summary>
    internal void TryParseLobbyCommand()
    {
        var args = Environment.GetCommandLineArgs();

        for (var i = 0; i < args.Length; i++) {
            if (!string.Equals(args[i], ConnectLobbyArg, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (ulong.TryParse(args.TryGet(i + 1, true), out var lobbyId) && lobbyId != 0) {
                ConnectLobby(lobbyId);
                return;
            }
        }
    }

    private static bool TryParseConnectString(string? connect, out ulong lobbyId)
    {
        lobbyId = 0;

        if (connect.IsEmpty()) {
            return false;
        }

        var parts = connect!.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++) {
            if (!string.Equals(parts[i], ConnectLobbyArg, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (ulong.TryParse(parts.TryGet(i + 1, true), out lobbyId) && lobbyId != 0) {
                return true;
            }
        }

        return false;
    }

#region Steam Callbacks

    private void OnLobbyCreated(LobbyCreated_t created)
    {
        // the game playing on its hidden server asked for none: a lobby of the server's, told on the same account,
        // is not this game's to fill in or to leave
        if (PlaysOnHiddenServer) {
            EmpLog.Information("Lobby {LobbyId} created on this account ignored: this game plays on its hidden server",
                created.m_ulSteamIDLobby);
            return;
        }

        // an answer nothing waits for any more (the session was reset, or a second lobby was asked before the first
        // came): kept, it would stay listed with this game's data and no key would ever be written in it, so whoever
        // joined it waited and failed (two entries of one game, live report 2026-11-01). Left at once
        if (!_creating) {
            if (created.m_eResult == EResult.k_EResultOK && created.m_ulSteamIDLobby != 0) {
                EmpLog.Warning("Lobby {LobbyId} created after it stopped being wanted: left", created.m_ulSteamIDLobby);
                SteamMatchmaking.LeaveLobby(new CSteamID(created.m_ulSteamIDLobby));
            }

            return;
        }

        _creating = false;
        if (created.m_eResult != EResult.k_EResultOK || created.m_ulSteamIDLobby == 0) {
            EmpLog.Warning("Lobby creation failed with {Result}",
                created.m_eResult);

            // opened by itself: a player alone and offline would get this window at every load
            if (!_quiet) {
                EmpPop.Block("emp_lobby_create_failed".lang(), created.m_eResult);
            }

            NetSession.Instance.ResetSession();
            return;
        }

        if (_quiet) {
            EmpLog.Information("Lobby created");
        } else {
            EmpPop.Information("emp_lobby_created".lang());
        }

        Current = created.m_ulSteamIDLobby;
        Current.SetGameServer(SteamUser.GetSteamID());

        // a hidden server tells its lobby to the game that started it, which invites with it
        if (EmpServer.Hidden) {
            HiddenServer.WriteServerLobby(created.m_ulSteamIDLobby);
        }

        // first: the lobby can be found as soon as its version is written
        if (_invisible) {
            Current[HiddenKey] = "1";
        }

        Current.GameVersion = core.version.GetText();
        Current.Name = SteamFriends.GetPersonaName();

        Current[EmpLobbyData.EmpVersion] = ModInfo.BuildVersion;
        Current[EmpLobbyData.GameBuild] = BuildVersionIntegrity.GameVersion;
        Current[EmpLobbyData.CurrentZone] = core.game?.activeZone?.NameWithLevel ?? "";
        Current[EmpLobbyData.World] = WorldHandover.WorldKey;
        Current[EmpLobbyData.Handover] = ElinNetHost.HandoverNumber.ToString();

        Current[EmpLobbyData.Public] = OpenToAll ? "1" : "0";
        Current[EmpLobbyData.ServerKind] = EmpServer.Hidden ? "hidden" : EmpServer.Requested ? "server" : "game";

        // the project's own public game: shown first and as official in everyone's list
        if (Signs && OfficialServer.Sign(Current.SteamId.m_SteamID) is { } signature) {
            Current[OfficialServer.SignatureKey] = signature;
            EmpLog.Information("Lobby signed as the official Elin Together server");
        }

        // the player keeping the peer-hosted public world: its ETWorld client asks a host certificate for this lobby
        PublicServerCertificate.LobbyOpened(Current.SteamId.m_SteamID);

        // the mods of the game, for who looks at the list of games before joining
        if (EmpConfig.Server.PublishMods.Value) {
            Current[EmpLobbyData.Mods] = ModList.Compact;
        }

        NetSession.Instance.SessionId = Current;

        UpdateRichPresence();
    }

    private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
    {
        var lobbyId = request.m_steamIDLobby;

        EmpLog.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby(lobbyId);
    }

    private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
    {
        if (!TryParseConnectString(request.m_rgchConnect, out var lobbyId)) {
            EmpLog.Warning("Unrecognized rich presence connect string {Connect}",
                request.m_rgchConnect);
            return;
        }

        EmpLog.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby(lobbyId);
    }

    private void OnLobbyEntered(LobbyEnter_t state)
    {
        LobbyEnter enter = state;

        // the hidden server's own lobby, entered on the same account as it was created: not this game's
        var entered = state.m_ulSteamIDLobby;
        if (PlaysOnHiddenServer && entered != _joining && entered != ((CSteamID)Current).m_SteamID) {
            EmpLog.Information("Lobby {LobbyId} entered on this account ignored: this game plays on its hidden server",
                entered);
            return;
        }

        _joining = 0;
        if (enter.Locked || enter.Response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
            EmpLog.Warning("Lobby enter refused with {Response}, locked {Locked}",
                enter.Response, enter.Locked);

            NetSession.Instance.ResetSession();

            // an attempt among others while coming back by itself, see NetReconnect
            if (!NetReconnect.Active) {
                EmpPop.Block("emp_lobby_enter_failed".lang(), enter.Response);
            }

            return;
        }

        Current = enter.Lobby;
        NetSession.Instance.SessionId = enter.Lobby;

        UpdateRichPresence();

        var me = Current.Me;
        if (me.IsOwner) {
            me.IsReady = true;
        } else {
            // a late answer after the client already gave up: nothing would ever join this game, do not stay in
            // the lobby as a silent member
            if (NetSession.Instance.Transport is not ElinNetClient) {
                EmpLog.Warning("Lobby {LobbyId} entered after the join was abandoned: leaving it", Current);
                NetSession.Instance.SessionId = 0;
                LeaveLobby();
                return;
            }

            foreach (var member in Current.Members) {
                Friends.Client.RequestUserInformation(member.user, true);
            }

            // a member learns the lobby's owner: a lobby that says it is the public server must be kept by the
            // account or the keeper its proof names (Steam gives a lobby to another member when its owner leaves,
            // with the old data still in it)
            if (OfficialServer.Claims(Current) && !OfficialServer.IsOfficial(Current)) {
                EmpLog.Warning("Leaving lobby {LobbyId}: it shows itself as the public server but its owner or proof " +
                               "does not match", Current);
                NetSession.Instance.ResetSession();
                EmpPop.Block("emp_public_server_unverified".lang());
                return;
            }

            var mod = Current[EmpLobbyData.EmpVersion] ?? "";
            var version = Current[EmpLobbyData.GameBuild] ?? "";
            if (!BuildVersionIntegrity.Ok(mod, version)) {
                EmpLog.Warning(
                    "Leaving lobby {LobbyId}: mod {ClientModVersion} vs {HostModVersion}, " +
                    "game {ClientGameVersion} vs {HostGameVersion}",
                    Current,
                    ModInfo.BuildVersion, mod,
                    BuildVersionIntegrity.GameVersion, version);

                NetSession.Instance.ResetSession();

                EmpPop.Debug("emp_version_rejected_client".Loc(BuildVersionIntegrity.Explain(mod, version)));

                return;
            }

            if (!Current.HasServer) {
                NetSession.Instance.ResetSession();

                EmpPop.Debug("emp_connection_no_server".lang());

                return;
            }

            // the host left this lobby and Steam gave it to one of its players: nobody in it lets anyone into the
            // game it names, a join would only wait and fail. Said plainly instead
            if (!HostKeeps(Current)) {
                EmpLog.Warning("Leaving lobby {LobbyId}: its owner {Owner} is not the game's host {Host}",
                    Current, SteamMatchmaking.GetLobbyOwner(Current).m_SteamID, Current.GameServer.id.m_SteamID);
                NetSession.Instance.ResetSession();
                EmpPop.Block("emp_lobby_host_gone".lang());
                return;
            }

            EmpPop.Information("emp_lobby_joined".lang(),
                Current[EmpLobbyData.EmpVersion]);

            ELayerCleanup.Cleanup<LayerHelp>();

            (NetSession.Instance.Transport as ElinNetClient)?.TryJoinCurrentLobbyGame();
        }
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
    {
        // (the key of a player who entered another lobby of this account was written in this one: that player
        // never found it and its join failed)
        if (update.m_ulSteamIDLobby != ((CSteamID)Current).m_SteamID) {
            return;
        }

        UpdateRichPresence();

        UserData user = update.m_ulSteamIDUserChanged;
        var state = (SteamNetLobbyMemberState)update.m_rgfChatMemberStateChange;

        Friends.Client.RequestUserInformation(user, true);

        EmpPop.Debug("emp_lobby_state_changed".lang(), new { user.Name, State = state });

        var me = Current.Me;
        if (me.IsOwner) {
            if (state == SteamNetLobbyMemberState.Entered) {
                var friend = SteamFriends.GetFriendRelationship(user);
                var known = IsKnownAccount(user);
                // banned from this world (ElinNetHostModeration): no key, out of the lobby
                if (ElinNetHost.Bans.ContainsKey(user) || ElinNetHost.MembersOnlyRefuses(user)) {
                    EmpLog.Information("Refused {RemoteIdentity}: banned from this world, or not one of its players (members only)", user);
                    Current.KickMember(user);
                } else if (friend == EFriendRelationship.k_EFriendRelationshipFriend || _invited.Contains(user) || known || OpenToAll) {
                    Current[$"connection_key_{user}"] =
                        SteamNetManager.ConnectionKeys[user] =
                            PlayerUidMaker.MakeConnectionKey(user);
                    EmpLog.Information("Connection ready for {RemoteIdentity} ({Why})",
                        user, known ? "account known to this world" : OpenToAll ? "public game" : "friend or invited");
                } else {
                    EmpLog.Information("Refused {RemoteIdentity}: not a friend, not invited, not known to this world",
                        user);
                    Current.KickMember(user);
                }
            } else {
                if (!me.IsReady) {
                    // host migrated to us
                    NetSession.Instance.ResetSession();
                    return;
                }

                Current.RemoveFromKickList(user);
            }
        }
    }

    /// <summary>
    ///     The host ticked "Public game": anybody who finds the game in the list comes in. Not a session opened by
    ///     itself (depot, shared world): those let only the accounts of their world in
    /// </summary>
    internal bool OpenToAll => EmpConfig.Server.PublicGame.Value && !_quiet;

    // a game anyone can join, hosted by the Elin Together account with its key (OfficialServer)
    private bool Signs => (OpenToAll || EmpServer.Requested) && OfficialServer.CanHost();

    /// <summary>
    ///     A session opened by itself lets the accounts of its world in. A session opened by hand never does
    /// </summary>
    private bool IsKnownAccount(UserData user)
    {
        if (!_quiet || KnownAccount is null) {
            return false;
        }

        try {
            ulong id = user;
            return KnownAccount(id);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not tell if {RemoteIdentity} is known to this world", user);
            return false;
        }
    }

    private void OnLobbyDataUpdate(LobbyDataUpdate_t update)
    {
        LobbyDataUpdateEventData data = update;

        // (the data of the lobbies of a list comes this way too: a kick out of another game is not this one's)
        if (data.lobby.IsOwner || data.lobby != Current) {
            return;
        }

        if (data.lobby.KickListContains(UserData.Me)) {
            // the host's friend/invited filter or a ban kicked us: say so instead of resetting silently
            EmpLog.Information("Kicked out of lobby {LobbyId} by the host", data.lobby);
            NetSession.Instance.ResetSession();
            EmpPop.Block("emp_lobby_not_allowed".lang());
        }
    }

    private void OnLobbyMatchListComplete(LobbyMatchList_t list)
    {
        var fetched = list.m_nLobbiesMatching;
        List<LobbyData> lobbies = [];

        for (var i = 0; i < fetched; ++i) {
            LobbyData lobby = SteamMatchmaking.GetLobbyByIndex(i);
            // (the lobby a world opens by itself is not for strangers)
            if (!lobby.IsValid || lobby.MemberCount == 0 ||
                (lobby[HiddenKey] == "1" && SteamFriends.GetFriendRelationship(lobby.GameServer.id) !=
                    EFriendRelationship.k_EFriendRelationshipFriend &&
                    !WorldHandover.IsTaker(lobby.GameServer.id.m_SteamID) &&
                    (_listWorld is null || lobby[EmpLobbyData.World] != _listWorld))) {
                continue;
            }

            // a lobby its host left (Steam gave it to a player in it): no game behind it any more
            if (!HostKeeps(lobby)) {
                continue;
            }

            lobbies.Add(lobby);
        }

        _deferOnComplete?.Invoke(OnePerHost(lobbies));
        _deferOnComplete = null;
    }

#endregion

    /// <summary>
    ///     The lobby is still kept by the account of the game it names (its game server): false when the host left and
    ///     Steam gave the lobby to one of its players. True when either is not known
    /// </summary>
    private static bool HostKeeps(LobbyData lobby)
    {
        try {
            var host = lobby.GameServer.id.m_SteamID;
            var owner = SteamMatchmaking.GetLobbyOwner(lobby.SteamId).m_SteamID;
            return host == 0 || owner == 0 || owner == host;
        } catch (Exception) {
            return true;
        }
    }

    /// <summary>
    ///     One game per host account: an account hosts one game (a player who plays on a hidden server hosts none
    ///     itself, its server does), so two lobbies of one account are a left-over and the live one. The hidden
    ///     server's is kept first, then the one with the most players, then the newest
    /// </summary>
    internal static LobbyData[] OnePerHost(IEnumerable<LobbyData> lobbies)
    {
        return lobbies
            .GroupBy(l => l.GameServer.id.m_SteamID is var host and not 0 ? host : l.SteamId.m_SteamID)
            .Select(g => g.OrderByDescending(l => l[EmpLobbyData.ServerKind] == "hidden")
                .ThenByDescending(l => l.MemberCount)
                .ThenByDescending(l => l.SteamId.m_SteamID)
                .First())
            .ToArray();
    }
}