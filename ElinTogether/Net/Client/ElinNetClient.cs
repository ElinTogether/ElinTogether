using System;
using ElinTogether.Common;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;
using ReflexCLI.UI;

namespace ElinTogether.Net;

internal partial class ElinNetClient : ElinNetBase
{
    private DateTime _lastTimeout = DateTime.Now;

    // the wait starts once the component is ready: its start (a release build patches the whole mod there, 20 s on
    // a slow PC) counted as time without an answer, and the guest gave up at its first frame (issue #24)
    private void Start()
    {
        _lastTimeout = DateTime.Now;
    }

    public override bool IsHost => false;
    public ISteamNetPeer Host => Socket.FirstPeer;
    public bool IsJoiningLobby { get; private set; }

    // realtime of the lobby join request while Steam has not answered with LobbyEnter (0: none pending). A slow PC
    // gets that answer 15-40 s late, so this wait has its own, longer limit than the no-answer timer
    private float _lobbyJoinRequestedAt;
    private const float LobbyJoinLimit = 60f;

    internal void MarkLobbyJoinRequested()
    {
        _lobbyJoinRequestedAt = UnityEngine.Time.realtimeSinceStartup;
    }

    internal void ClearLobbyJoinRequested()
    {
        _lobbyJoinRequestedAt = 0f;
    }

    /// <summary>
    ///     Connected through the local udp port instead of a steam lobby, for debugging
    /// </summary>
    public bool IsLocalConnection { get; private set; }

    public bool IsDirectConnection { get; private set; }

    protected override void Update()
    {
        base.Update();

        if (IsZoneSession) {
            // a crashed zone host only shows as a local timeout (no ClosedByPeer), fall back to the host link
            if (!IsConnected) {
                EmpLog.Warning("Lost the zone session host");
                EndSession(EmpDisconnectInfo.RemoteClosed);
            }

            return;
        }

        UpdateTravelCheckpoint();
        UpdateHandoffWait();
        UpdateResumeTravel();
        UpdateQuestInvite();
        UpdateTransferLock();
        UpdateSoftRejoinWait();
        UpdateSoftStayWait();
        UpdateTravelWatchdog();
        UpdateHostLeft();
        Helper.OwnSettings.WatchRespawn();

        if (IsConnected) {
            _lastTimeout = DateTime.Now;
            return;
        }

        if (IsJoiningLobby && Session.Lobby.Current.HasServer) {
            if (long.TryParse(Session.Lobby.Current[$"connection_key_{UserData.Me}"], out var key) && key != 0L) {
                IsJoiningLobby = false;
                ConnectSteamUser(Session.Lobby.Current.GameServer.id);
            }
        }

#if DEBUG
        // a debug build waits on a dead link for good (breakpoints, slow starts), unless a test asks for what a
        // release build does: emp.cut_link, emp.link_timeout
        if (!UseTimeout) {
            return;
        }
#endif
        var elapsed = DateTime.Now - _lastTimeout;
        var limit = (double)EmpConfig.Policy.Timeout.Value;
        if (_lobbyJoinRequestedAt > 0f) {
            elapsed = TimeSpan.FromSeconds(UnityEngine.Time.realtimeSinceStartup - _lobbyJoinRequestedAt);
            limit = LobbyJoinLimit;
        }

        if (elapsed.TotalSeconds > limit) {
            NetReconnect.Begin();
            // coming back by itself says so on its own
            if (!NetReconnect.Active) {
                EmpPop.Block((IsDirectConnection ? "emp_ui_timeout" : "emp_ui_timeout_steam").lang());
            }

            EndSession(EmpDisconnectInfo.Timeout);
        }
    }

#if DEBUG
    internal static bool UseTimeout;
#endif

    /// <summary>
    ///     When the host said it leaves (0: it did not)
    /// </summary>
    internal float HostLeftAt { get; set; }

    /// <summary>
    ///     The host said it leaves and closed its link, but this end may still read it as open (a close that never
    ///     arrived, seen on the bench 2026-10-26: the guest stayed frozen in a dead game while the host reloaded its
    ///     world). Two seconds later it is closed here, and the way back starts as after a dropped link
    /// </summary>
    private void UpdateHostLeft()
    {
        if (HostLeftAt <= 0f || !IsConnected || UnityEngine.Time.realtimeSinceStartup - HostLeftAt < 2f) {
            return;
        }

        EmpLog.Information("The host left and its link is still open here: closing it");
        HostLeftAt = 0f;
        NetReconnect.Begin(true);
        EndSession(EmpDisconnectInfo.HostShutdown);
    }

    /// <summary>
    ///     How to join the same game again once this component is gone: the same local port or address, else
    ///     the same Steam lobby. Null when nothing says where that game is
    /// </summary>
    internal Action? Rejoin
    {
        get {
            if (_rejoin is not null) {
                return _rejoin;
            }

            var lobby = Session.Lobby.Current;
            if (!lobby.IsValid) {
                return null;
            }

            return () => Session.Lobby.ConnectLobby(lobby);
        }
    }

    private Action? _rejoin;

    /// <summary>
    ///     Where this game is joined, in words a restarted Elin can read (ModFetch): "lobby id", "address host:port",
    ///     "port n" on the bench. Empty: nothing says where
    /// </summary>
    internal string ReturnTo
    {
        get {
            ulong lobby = Session.Lobby.Current;
            return _returnTo ?? (lobby != 0 ? $"lobby {lobby}" : "");
        }
    }

    private string? _returnTo;

    public void ConnectLocalPort(ushort port = 0)
    {
        port = port == 0 ? (ushort)EmpConstants.LocalPort : port;
        Stop();
        IsLocalConnection = true;
        _rejoin = () => Session.InitializeComponent<ElinNetClient>().ConnectLocalPort(port);
        _returnTo = $"port {port}";
        Socket.Connect(port);
    }

    /// <summary>
    ///     To a server by its address ("host:port"), as one joins a Minecraft server. Like a local connection
    ///     it does not go through the Steam lobby; players still reach each other's maps through Steam
    /// </summary>
    public void ConnectAddress(string address)
    {
        Stop();
        IsLocalConnection = false;
        IsDirectConnection = true;
        _rejoin = () => Session.InitializeComponent<ElinNetClient>().ConnectAddress(address);
        _returnTo = $"address {address}";
        Socket.Connect(address);
    }

    public void ConnectSteamUser(UserData steamId, int vport = 0)
    {
        Stop();
        IsLocalConnection = false;
        Socket.Connect(steamId, vport);
    }

    protected override void RegisterPackets()
    {
        Router.ShouldReceivePacket = ShouldReceiveHostPacket;

        // delta
        Router.RegisterHandler<ZoneDataResponse>(OnZoneDataResponse);
        Router.RegisterHandler<ZoneActivateResponse>(OnZoneActivateResponse);
        Router.RegisterHandler<WorldStateSnapshot>(OnWorldStateSnapshot);
        Router.RegisterHandler<WorldStateDeltaList>(OnWorldStateDeltaResponse);

        // integrity
        Router.RegisterHandler<NetIntegrityRequest>(OnNetHandshakeRequest);
        Router.RegisterHandler<NetIntegrityRejected>(OnNetHandshakeRejected);

        // source validation
        Router.RegisterHandler<SourceValidationRequest>(OnSourceValidationRequest);
        Router.RegisterHandler<SourceValidationFailed>(OnSourceValidationFailed);

        // session
        Router.RegisterHandler<SessionNewPlayerRequest>(OnSessionNewPlayerRequest);
        Router.RegisterHandler<SessionCharaSelectRequest>(OnSessionCharaSelectRequest);
        Router.RegisterHandler<SessionCharaExportData>(OnSessionCharaExportData);
        Router.RegisterHandler<SaveDataProbe>(OnSaveDataProbe);
        Router.RegisterHandler<SteamLobbyRequest>(OnSteamLobbyRequest);
        Router.RegisterHandler<SessionPlayersSnapshot>(OnSessionStatesUpdate);
        Router.RegisterHandler<NetSessionRules>(OnSessionRulesUpdate);
        Router.RegisterHandler<SessionReconnectRequest>(OnSessionReconnectRequest);

        // independent travel
        Router.RegisterHandler<ZoneLeaseGrant>(OnZoneLeaseGrant);
        Router.RegisterHandler<ZoneLeaseDepart>(OnZoneLeaseDepart);
        Router.RegisterHandler<ZoneLeaseDenied>(OnZoneLeaseDenied);
        Router.RegisterHandler<ZoneLeaseRecall>(OnZoneLeaseRecall);
        Router.RegisterHandler<ZoneSoftRejoin>(OnZoneSoftRejoin);
        Router.RegisterHandler<ZoneGuestRequest>(OnZoneGuestRequest);
        Router.RegisterHandler<ZoneGuestLeft>(OnZoneGuestLeft);
        Router.RegisterHandler<ShippingPayout>(OnShippingPayout);

        // copy of the host's world, see Net/Handover
        RegisterWorldCopy();
    }

    internal override void Stop()
    {
        base.Stop();

        // a zone session closes, the game goes on with the host link
        if (!core.IsGameStarted || IsZoneSession) {
            return;
        }

        scene.Init(Scene.Mode.Title);
    }

#region Net Events

    /// <summary>
    ///     Net event: On connected to host
    /// </summary>
    protected override void OnPeerConnected(ISteamNetPeer host)
    {
        if (!host.IsConnected) {
            // (an attempt among others while coming back by itself, see NetReconnect: only the give-up blocks)
            if (NetReconnect.Active) {
                EmpPop.Information("emp_error_connection".lang());
            } else {
                EmpPop.Block("emp_error_connection".lang());
            }
            EndSession(EmpDisconnectInfo.RemoteClosed);
            return;
        }

        // reconnects
        BeginHandshake();

        EmpPop.Information("emp_connecting_host".lang(), Host);

        this.StartDeferredCoroutine(StartWorldStateUpdate, () => core.IsGameStarted);

#if DEBUG
        if (!IsDebugGuiActive) {
            StartDebugGui();
        }
#endif
    }

    /// <summary>
    ///     Net event: On disconnected from host.
    ///     Fully clean up resources and return to title.
    /// </summary>
    protected override void OnPeerDisconnected(ISteamNetPeer host, string disconnectInfo)
    {
        StopWorldStateUpdate();
        StopAllCoroutines();

        if (ReflexUIManager.IsConsoleOpen()) {
            ReflexUIManager.StaticClose();
        }

        EmpLog.Warning("Disconnected from host: {Reason}",
            disconnectInfo);

        if (IsZoneSession) {
            // the host link reacts, see ElinNetClient.OnZoneSessionEnded
            EndSession(disconnectInfo);
            return;
        }

        var reason = Router.DisconnectReason;
        // (the host said it leaves and another guest takes the world over: that game is the one joined again)
        // (the host said it leaves: it may come back, a world reloaded or a game restarted)
        if (EmpDisconnectInfo.IsLinkLost(reason) || WorldHandover.Following || HostLeftAt > 0f) {
            NetReconnect.Begin(HostLeftAt > 0f && !EmpDisconnectInfo.IsLinkLost(reason));
        } else if (reason != EmpDisconnectInfo.HostShutdown) {
            // turned away, kicked, or the player's own way out: no coming back by itself.
            // HostShutdown is not told apart: it is also how any component of ours closes its link
            NetReconnect.Stop();
        }

        Session.ResetSession();

        // coming back by itself says so on its own
        // (blocks only when the player is turned away: its own way out, which closes the link as HostShutdown, or a
        // cancel of its own, is a line of the log; a dialog there stood in the way of joining again, 2026-10-26)
        if (!NetReconnect.Active) {
            if (reason is EmpDisconnectInfo.HostShutdown or EmpDisconnectInfo.ClientCancel) {
                EmpPop.Information("emp_disconnected_host".Loc(disconnectInfo));
            } else {
                EmpPop.Block("emp_disconnected_host".Loc(disconnectInfo));
            }
        }
    }

#endregion
}