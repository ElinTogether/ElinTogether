using System;
using System.Collections.Generic;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Steam;
using ElinTogether.LangMod;
using HeathenEngineering.SteamworksIntegration;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net.Steam;

public class SteamNetLobbyManager : EClass
{
    private readonly HashSet<UserData> _blocked = [];
    private readonly HashSet<UserData> _invited = [];
    private Action<LobbyData[]>? _deferOnComplete;
    private bool _shutdown;

    public LobbyData Current;

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

        SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
    }

    /// <summary>
    ///     Leave current lobby if it's valid
    /// </summary>
    public void LeaveLobby()
    {
        Current.Leave();
        Current = CSteamID.Nil;
    }

    /// <summary>
    ///     Connect by lobby id
    /// </summary>
    public void ConnectLobby(LobbyData lobby)
    {
        if (Current != lobby) {
            LeaveLobby();
        }

        if (core.IsGameStarted) {
            EMono.scene.Init(Scene.Mode.Title);
        }

        ELayerCleanup.Cleanup<LayerHelp>();

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
    ///     Parse from steam launch args
    /// </summary>
    internal void TryParseLobbyCommand()
    {
        ulong lobbyId = 0;
        var args = Environment.GetCommandLineArgs();

        for (var i = 0; i < args.Length; i++) {
            if (!string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (ulong.TryParse(args.TryGet(i + 1, true), out lobbyId)) {
                break;
            }
        }

        if (lobbyId != 0) {
            ConnectLobby(lobbyId);
        }
    }

#region Steam Callbacks

    private void OnLobbyCreated(LobbyCreated_t created)
    {
        EmpPop.Information("emp_lobby_created".lang());

        Current = created.m_ulSteamIDLobby;
        Current.SetGameServer(SteamUser.GetSteamID());

        Current.GameVersion = core.version.GetText();
        Current.Name = SteamFriends.GetPersonaName();

        Current[EmpLobbyData.EmpVersion] = ModInfo.BuildVersion;
        Current[EmpLobbyData.CurrentZone] = core.game?.activeZone?.NameWithLevel ?? "";

        NetSession.Instance.SessionId = Current;
    }

    private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
    {
        var lobbyId = request.m_steamIDLobby;

        EmpPop.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby(lobbyId);
    }

    private void OnLobbyEntered(LobbyEnter_t state)
    {
        LobbyEnter enter = state;
        if (enter.Locked || enter.Response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
            LeaveLobby();
            EmpPop.Information("Cannot join lobby");
            return;
        }

        Current = enter.Lobby;
        NetSession.Instance.SessionId = enter.Lobby;

        // assign game version
        var me = Current.Me;
        me.GameVersion = core.version.GetText();

        // assign friend grouping
        var sessionKey = NetSession.Instance.SessionId.ToString();
        SteamFriends.SetRichPresence("steam_player_group", sessionKey);
        SteamFriends.SetRichPresence("steam_player_group_size", Current.MemberCount.ToString());

        if (Current.IsOwner) {
            // assign steam rich presence join key
            //SteamFriends.SetRichPresence("connect", sessionKey);
        } else {
            if (me.GameVersion != Current.GameVersion) {
                LeaveLobby();

                EmpPop.Debug("emp_connection_rejected".Loc(
                    Current.GameVersion.TagColor(Color.red),
                    ModInfo.BuildVersion.TagColor(Color.green)));

                return;
            }

            if (!Current.HasServer) {
                LeaveLobby();
                return;
            }

            EmpPop.Information("emp_lobby_joined".lang(),
                Current[EmpLobbyData.EmpVersion]);

            ELayerCleanup.Cleanup<LayerHelp>();
        }
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
    {
        UserData user = update.m_ulSteamIDUserChanged;
        var state = (SteamNetLobbyMemberState)update.m_rgfChatMemberStateChange;

        SteamUserName.PinUserName(update.m_ulSteamIDUserChanged, name => EmpPop.Debug(
            "emp_lobby_state_changed".lang(), new { Name = name, State = state }));

        if (Current.IsOwner) {
            if (state == SteamNetLobbyMemberState.Entered) {
                var friend = SteamFriends.GetFriendRelationship(user);
                if (friend == EFriendRelationship.k_EFriendRelationshipFriend || _invited.Contains(user)) {
                    Current[$"connection_key_{(ulong)user}"] =
                        SteamNetManager.ConnectionKeys[user] =
                            PlayerUidMaker.MakeConnectionKey(user);
                    EmpLog.Information("Connection ready for {RemoteIdentity}",
                        user);
                } else {
                    Current.KickMember(user);
                }
            } else {
                Current.RemoveFromKickList(user);
            }
        }

        if (user == Current.Owner.user && state == SteamNetLobbyMemberState.Left) {
            NetSession.Instance.RemoveComponent();
            LeaveLobby();
        }
    }

    private void OnLobbyDataUpdate(LobbyDataUpdate_t update)
    {
        LobbyDataUpdateEventData data = update;

        if (data.lobby.IsOwner) {
            return;
        }

        if (data.lobby.KickListContains(UserData.Me)) {
            LeaveLobby();
        }

        if (long.TryParse(Current[$"connection_key_{(ulong)UserData.Me}"], out var key) && key != 0L) {
            if (data.lobby.HasServer) {
                // connect as clients
                if (NetSession.Instance.Connection is not ElinNetClient) {
                    NetSession.Instance
                        .InitializeComponent<ElinNetClient>()
                        .ConnectSteamUser(data.lobby.GameServer.id);
                }
            }
        }
    }

    private void OnLobbyMatchListComplete(LobbyMatchList_t list)
    {
        var fetched = list.m_nLobbiesMatching;
        List<LobbyData> lobbies = [];

        for (var i = 0; i < fetched; ++i) {
            var lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
            if (lobbyId == CSteamID.Nil) {
                continue;
            }

            lobbies.Add(lobbyId);
        }

        _deferOnComplete?.Invoke(lobbies.ToArray());
        _deferOnComplete = null;
    }

#endregion
}