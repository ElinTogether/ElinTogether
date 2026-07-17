using System.Diagnostics;
using ElinTogether.Common;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using ReflexCLI.UI;
using Steamworks;

namespace ElinTogether.Net;

internal partial class ElinNetClient : ElinNetBase
{
    public override bool IsHost => false;
    public ISteamNetPeer Host => Socket.FirstPeer;

    public void ConnectLocalPort(ushort port = EmpConstants.LocalPort)
    {
        Stop();
        Socket.Connect(port);
    }

    public void ConnectSteamUser(ulong steamId)
    {
        Stop();
        Socket.Connect(new CSteamID(steamId));
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
        Router.RegisterHandler<SourceListSync>(OnSourceListSync);

        // session
        Router.RegisterHandler<SessionNewPlayerRequest>(OnSessionNewPlayerRequest);
        Router.RegisterHandler<SaveDataProbe>(OnSaveDataProbe);
        Router.RegisterHandler<SteamLobbyRequest>(OnSteamLobbyRequest);
        Router.RegisterHandler<SessionPlayersSnapshot>(OnSessionStatesUpdate);
        Router.RegisterHandler<NetSessionRules>(OnSessionRulesUpdate);
    }

    protected override void DisconnectInactive()
    {
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
        Session.SetPhase(ConnectionPhase.Authenticating);
        EmpPop.Information("Connecting to host {@Peer}",
            Host);

        // CLIENT-ONLY
        var sw = Stopwatch.StartNew();
        while (host.Name is null && sw.ElapsedMilliseconds <= 500) {
            // do a spin wait here to pin the username
            // ignore if steam can't respond in 500ms
        }

        if (host.Name is null) {
            EmpLog.Warning("Host {Uid} name resolution timed out", host.Uid);
        }

        this.StartDeferredCoroutine(StartWorldStateUpdate, () => core.IsGameStarted);

#if DEBUG
        DebugProgress ??= EGui.CreatePopup(() => new(BuildDebugInfo()), _ => false, 1f);
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

        EmpLog.Warning("Disconnected from host (phase={Phase}): {Reason}",
            Session.CurrentPhase, disconnectInfo);

        if (core.IsGameStarted) {
            scene.Init(Scene.Mode.Title);
        }

        EmpPop.Information("Disconnected from host\n{DisconnectInfo}",
            disconnectInfo);

        Session.RemoveComponent();
    }

#endregion
}