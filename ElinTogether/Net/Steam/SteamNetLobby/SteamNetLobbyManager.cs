using System;
using System.Collections.Generic;
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

    private readonly HashSet<UserData> _blocked = [];
    private readonly HashSet<UserData> _invited = [];

    public LobbyData Current;
    private Action<LobbyData[]>? _deferOnComplete;
    private bool _shutdown;

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
    public void CreateLobby(SteamNetLobbyType type = SteamNetLobbyType.Public, int maxPlayers = 16)
    {
        LeaveLobby();

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

#if DEBUG
        lobbyType = ELobbyType.k_ELobbyTypePrivateUnique;
#endif

        SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
    }

    /// <summary>
    ///     Leave current lobby if it's valid
    /// </summary>
    public void LeaveLobby()
    {
        Current.Leave();
        Current = CSteamID.Nil;
        SteamFriends.ClearRichPresence();
    }

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

        if (NetSession.Instance.Connection is not ElinNetClient) {
            NetSession.Instance.InitializeComponent<ElinNetClient>();
        }

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
        // already friends
        SteamFriends.ActivateGameOverlayInviteDialog(Current);
    }

    /// <summary>
    ///     Fetch all current online lobbies
    /// </summary>
    public void GetOnlineLobbies(Action<LobbyData[]> onComplete)
    {
        _deferOnComplete = onComplete;
#if !DEBUG
        SteamMatchmaking.AddRequestLobbyListStringFilter("EmpVersion", ModInfo.BuildVersion, 0);
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
        if (created.m_eResult != EResult.k_EResultOK || created.m_ulSteamIDLobby == 0) {
            EmpLog.Warning("Lobby creation failed with {Result}",
                created.m_eResult);

            EmpPop.Information("emp_lobby_create_failed".lang(), created.m_eResult);

            NetSession.Instance.ResetSession();
            return;
        }

        EmpPop.Information("emp_lobby_created".lang());

        Current = created.m_ulSteamIDLobby;
        Current.SetGameServer(SteamUser.GetSteamID());

        Current.GameVersion = core.version.GetText();
        Current.Name = SteamFriends.GetPersonaName();

        Current[EmpLobbyData.EmpVersion] = ModInfo.BuildVersion;
        Current[EmpLobbyData.CurrentZone] = core.game?.activeZone?.NameWithLevel ?? "";

        NetSession.Instance.SessionId = Current;

        UpdateRichPresence();
    }

    private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
    {
        var lobbyId = request.m_steamIDLobby;

        EmpPop.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby(lobbyId);
    }

    private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
    {
        if (!TryParseConnectString(request.m_rgchConnect, out var lobbyId)) {
            EmpLog.Warning("Unrecognized rich presence connect string {Connect}",
                request.m_rgchConnect);
            return;
        }

        EmpPop.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby(lobbyId);
    }

    private void OnLobbyEntered(LobbyEnter_t state)
    {
        LobbyEnter enter = state;
        if (enter.Locked || enter.Response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
            EmpLog.Warning("Lobby enter refused with {Response}, locked {Locked}",
                enter.Response, enter.Locked);

            NetSession.Instance.ResetSession();

            EmpPop.Information("emp_lobby_enter_failed".lang(), enter.Response);
            return;
        }

        Current = enter.Lobby;
        NetSession.Instance.SessionId = enter.Lobby;

        UpdateRichPresence();

        var me = Current.Me;
        if (me.IsOwner) {
            me.IsReady = true;
        } else {
            foreach (var member in Current.Members) {
                Friends.Client.RequestUserInformation(member.user, true);
            }

            if (Current.GameVersion != core.version.GetText()) {
                NetSession.Instance.ResetSession();

                EmpPop.Debug("emp_connection_rejected".Loc(
                    Current.GameVersion.TagColor(Color.red),
                    ModInfo.BuildVersion.TagColor(Color.green)));

                return;
            }

            if (!Current.HasServer) {
                NetSession.Instance.ResetSession();

                EmpPop.Debug("emp_connection_no_server".lang());

                return;
            }

            EmpPop.Information("emp_lobby_joined".lang(),
                Current[EmpLobbyData.EmpVersion]);

            ELayerCleanup.Cleanup<LayerHelp>();

            (NetSession.Instance.Connection as ElinNetClient)?.TryJoinCurrentLobbyGame();
        }
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
    {
        UpdateRichPresence();

        UserData user = update.m_ulSteamIDUserChanged;
        var state = (SteamNetLobbyMemberState)update.m_rgfChatMemberStateChange;

        Friends.Client.RequestUserInformation(user, true);

        EmpPop.Debug("emp_lobby_state_changed".lang(), new { user.Name, State = state });

        var me = Current.Me;
        if (me.IsOwner) {
            if (state == SteamNetLobbyMemberState.Entered) {
                var friend = SteamFriends.GetFriendRelationship(user);
                if (friend == EFriendRelationship.k_EFriendRelationshipFriend || _invited.Contains(user)) {
                    Current[$"connection_key_{user}"] =
                        SteamNetManager.ConnectionKeys[user] =
                            PlayerUidMaker.MakeConnectionKey(user);
                    EmpLog.Information("Connection ready for {RemoteIdentity}",
                        user);
                } else {
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

    private void OnLobbyDataUpdate(LobbyDataUpdate_t update)
    {
        LobbyDataUpdateEventData data = update;

        if (data.lobby.IsOwner) {
            return;
        }

        if (data.lobby.KickListContains(UserData.Me)) {
            NetSession.Instance.ResetSession();
        }
    }

    private void OnLobbyMatchListComplete(LobbyMatchList_t list)
    {
        var fetched = list.m_nLobbiesMatching;
        List<LobbyData> lobbies = [];

        for (var i = 0; i < fetched; ++i) {
            LobbyData lobby = SteamMatchmaking.GetLobbyByIndex(i);
            if (!lobby.IsValid || lobby.MemberCount == 0) {
                continue;
            }

            lobbies.Add(lobby);
        }

        _deferOnComplete?.Invoke([..lobbies]);
        _deferOnComplete = null;
    }

#endregion
}