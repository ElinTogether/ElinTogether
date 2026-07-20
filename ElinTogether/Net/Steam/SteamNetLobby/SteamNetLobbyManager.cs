using System;
using System.Collections.Generic;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Steam;
using Steamworks;

namespace ElinTogether.Net.Steam;

public class SteamNetLobbyManager : EClass
{
    private readonly HashSet<ulong> _blocked = [];
    private Action<SteamNetLobby[]>? _deferOnComplete;
    private bool _shutdown;

    internal SteamNetLobbyManager()
    {
        RegisterCallbacks();
    }

    public SteamNetLobby? Current { get; private set; }

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
        SteamCallback<GameLobbyJoinRequested_t>.Add(OnLobbyJoinRequested);
        //SteamCallback<GameRichPresenceJoinRequested_t>.Add(OnRichPresenceJoinRequested);
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
        SteamCallback<GameLobbyJoinRequested_t>.Remove(OnLobbyJoinRequested);
        SteamCallback<LobbyEnter_t>.Remove(OnLobbyEntered);
        SteamCallback<LobbyMatchList_t>.Remove(OnLobbyMatchListComplete);

        SteamCallback<LobbyCreated_t>.Shutdown();
        SteamCallback<LobbyChatUpdate_t>.Shutdown();
        SteamCallback<GameLobbyJoinRequested_t>.Shutdown();
        SteamCallback<LobbyEnter_t>.Shutdown();
        SteamCallback<LobbyMatchList_t>.Shutdown();

        _deferOnComplete = null;
    }

    /// <summary>
    ///     Create a new lobby. We do this automatically on Host
    /// </summary>
    public void CreateLobby(SteamNetLobbyType type = SteamNetLobbyType.Invite, int maxPlayers = 16)
    {
        LeaveLobby();

        EmpLog.Information("Creating steam {LobbyType} lobby",
            type);

        var lobbyType = type switch {
            SteamNetLobbyType.Public => ELobbyType.k_ELobbyTypePublic,
            SteamNetLobbyType.Friend => ELobbyType.k_ELobbyTypeFriendsOnly,
            // we use public to be able to search in list
            // though we do not join from here
            SteamNetLobbyType.Invite => ELobbyType.k_ELobbyTypePublic,
            _ => throw new ArgumentOutOfRangeException(nameof(SteamNetLobbyType), type, null),
        };

        SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
    }

    /// <summary>
    ///     Leave current lobby if it's valid
    /// </summary>
    public void LeaveLobby()
    {
        if (Current is not null) {
            SteamMatchmaking.LeaveLobby(Current.LobbyId);
            EmpLog.Information("Left steam lobby");
        }

        Current = null;
    }

    /// <summary>
    ///     Connect by lobby id
    /// </summary>
    public void ConnectLobby(ulong lobbyId)
    {
        var steamLobby = (CSteamID)lobbyId;
        if (Current is not null && Current.LobbyId != steamLobby) {
            LeaveLobby();
        }

        Challenge((ulong)SteamUser.GetSteamID());

        if (core.IsGameStarted) {
            EMono.scene.Init(Scene.Mode.Title);
        }

        ELayerCleanup.Cleanup<LayerHelp>();

        SteamMatchmaking.JoinLobby(steamLobby);
    }

    /// <summary>
    ///     Invite by steam user id
    /// </summary>
    public void InviteSteamUser(ulong steamId64)
    {
        Challenge(steamId64);

        if (Current is not null) {
            SteamMatchmaking.InviteUserToLobby(Current.LobbyId, (CSteamID)steamId64);
        }
    }

    /// <summary>
    ///     Invite by opening up overlay, requires launching from steam
    /// </summary>
    public void InviteSteamOverlay()
    {
        Challenge((ulong)SteamUser.GetSteamID());

        if (Current is not null) {
            SteamFriends.ActivateGameOverlayInviteDialog(Current.LobbyId);
        }
    }

    /// <summary>
    ///     Fetch all current online lobbies
    /// </summary>
    public void GetOnlineLobbies(Action<SteamNetLobby[]> onComplete)
    {
        Challenge((ulong)SteamUser.GetSteamID());

        _deferOnComplete = onComplete;
#if !DEBUG
        SteamMatchmaking.AddRequestLobbyListStringFilter("EmpVersion", ModInfo.BuildVersion, 0);
#endif
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

    internal void Challenge(ulong userId)
    {
        if (_blocked.Contains(userId)) {
            throw new NotSupportedException("Target user is blocked");
        }
    }

#region Steam Callbacks

    private void OnLobbyCreated(LobbyCreated_t lobby)
    {
        EmpPop.Information("emp_lobby_created".lang());

        Current = new((CSteamID)lobby.m_ulSteamIDLobby);

        Current.SetLobbyData(EmpLobbyData.EmpVersion, ModInfo.BuildVersion);

        // add our custom lobby data
        Current.SetLobbyData(EmpLobbyData.OwnerName, SteamFriends.GetPersonaName());
        Current.SetLobbyData(EmpLobbyData.GameVersion, core.version.GetText());
        Current.SetLobbyData(EmpLobbyData.CurrentZone, core.game?.activeZone?.NameWithLevel ?? "");

        NetSession.Instance.SessionId = lobby.m_ulSteamIDLobby;
    }

    private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
    {
        var lobbyId = request.m_steamIDLobby;

        EmpPop.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby((ulong)lobbyId);
    }

    private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
    {
        var lobbyId = request.m_rgchConnect;

        EmpPop.Information("emp_lobby_join_request".lang(), lobbyId);

        ConnectLobby(ulong.Parse(lobbyId));
    }

    private void OnLobbyEntered(LobbyEnter_t state)
    {
        Current = new((CSteamID)state.m_ulSteamIDLobby);
        Current.RefreshData();

        NetSession.Instance.SessionId = state.m_ulSteamIDLobby;

        // assign friend grouping
        var sessionKey = NetSession.Instance.SessionId.ToString();
        SteamFriends.SetRichPresence("steam_player_group", sessionKey);

        var host = Current.GetLobbyOwner();
        if (host == SteamUser.GetSteamID()) {
            // assign steam rich presence join key
            //SteamFriends.SetRichPresence("connect", sessionKey);
            return;
        }

        EmpPop.Information("emp_lobby_joined".lang(), Current.EmpVersion);

        ELayerCleanup.Cleanup<LayerHelp>();

        // connect automatically as clients
        if (NetSession.Instance.Connection is not ElinNetClient) {
            NetSession.Instance
                .InitializeComponent<ElinNetClient>()
                .ConnectSteamUser((ulong)host);
        }
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
    {
        var member = (CSteamID)update.m_ulSteamIDUserChanged;
        var state = (SteamNetLobbyMemberState)update.m_rgfChatMemberStateChange;

        SteamUserName.PinUserName(update.m_ulSteamIDUserChanged, name => EmpPop.Information(
            "emp_lobby_state_changed".lang(), new { Name = name, State = state }));

        if (member != Current?.GetLobbyOwner()) {
            return;
        }

        // we also leave lobby if host is gone
        NetSession.Instance.RemoveComponent();
        LeaveLobby();
    }

    private void OnLobbyMatchListComplete(LobbyMatchList_t list)
    {
        var fetched = list.m_nLobbiesMatching;
        List<SteamNetLobby> lobbies = [];

        for (var i = 0; i < fetched; ++i) {
            var lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
            if (lobbyId == CSteamID.Nil) {
                continue;
            }

            var lobby = new SteamNetLobby(lobbyId);
            lobby.RefreshData();

#if DEBUG
            if (lobby.GetLobbyOwner().m_SteamID is 76561198254677013UL or 76561198412175578UL) {
                continue;
            }
#endif

            lobbies.Add(lobby);
        }

        _deferOnComplete?.Invoke(lobbies.ToArray());
        _deferOnComplete = null;
    }

#endregion
}