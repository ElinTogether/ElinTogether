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

    public override bool IsHost => false;
    public ISteamNetPeer Host => Socket.FirstPeer;
    public bool IsJoiningLobby { get; private set; }

    protected override void Update()
    {
        base.Update();

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

#if !DEBUG
        var elapsed = DateTime.Now - _lastTimeout;
        if (elapsed.TotalSeconds > EmpConfig.Policy.Timeout.Value) {
            EmpPop.Information("emp_ui_timeout".lang());
            Session.ResetSession();
        }
#endif
    }

    public void ConnectLocalPort(ushort port = EmpConstants.LocalPort)
    {
        Stop();
        Socket.Connect(port);
    }

    public void ConnectSteamUser(UserData steamId)
    {
        Stop();
        Socket.Connect(steamId);
    }

    protected override void RegisterPackets()
    {
        // delta
        Router.RegisterHandler<ZoneDataResponse>(OnZoneDataResponse);
        Router.RegisterHandler<ZoneActivateResponse>(OnZoneActivateResponse);
        Router.RegisterHandler<WorldStateSnapshot>(OnWorldStateSnapshot);
        Router.RegisterHandler<WorldStateDeltaList>(OnWorldStateDeltaResponse);

        // source validation
        Router.RegisterHandler<SourceValidationRequest>(OnSourceValidationRequest);
        Router.RegisterHandler<SourceValidationFailed>(OnSourceValidationFailed);

        // session
        Router.RegisterHandler<SessionNewPlayerRequest>(OnSessionNewPlayerRequest);
        Router.RegisterHandler<SaveDataProbe>(OnSaveDataProbe);
        Router.RegisterHandler<SteamLobbyRequest>(OnSteamLobbyRequest);
        Router.RegisterHandler<SessionPlayersSnapshot>(OnSessionStatesUpdate);
        Router.RegisterHandler<NetSessionRules>(OnSessionRulesUpdate);
        Router.RegisterHandler<SessionReconnectRequest>(OnSessionReconnectRequest);
    }

    internal override void Stop()
    {
        base.Stop();

        if (!core.IsGameStarted) {
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
            EmpPop.Information("emp_error_connection".lang());
            Session.ResetSession();
            return;
        }

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

        if (core.IsGameStarted) {
            scene.Init(Scene.Mode.Title);
        }

        EmpPop.Information("emp_disconnected_host".Loc(disconnectInfo));

        Session.ResetSession();
    }

#endregion
}