using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;

namespace ElinTogether.Net;

internal partial class ElinNetHost : ElinNetBase
{
    internal readonly Dictionary<int, NetPeerState> States = [];

    public override bool IsHost => true;

    internal void StartServer(bool localUdp = false)
    {
        Stop();
        StopWorldStateUpdate();

        if (!core.IsGameStarted || player?.chara?.homeBranch?.owner is null) {
            EmpLog.Warning("Cannot start server: game not started or no land claimed");
            EmpPop.Debug("emp_ui_unclaimed_zone".lang());
            Session.ResetSession();
            return;
        }

        Session.Lobby.CreateLobby();

        try {
            if (localUdp) {
                Socket.StartServerUdp();
            } else {
                Socket.StartServerSdr();
            }
        } catch {
            Session.ResetSession();
            throw;
        }

        Scheduler.Subscribe(DisconnectInactive, 1);

        // host also registers self state
        var selfState = States[0] = new() {
            Index = 0,
            User = UserData.Me,
            CharaUid = player.uidChara,
        };

        // setup session states
        Session.Player = pc;
        Session.Self = selfState;
        Session.CurrentPlayers.Add(selfState);
        Session.SharedSpeed = NetSession.Instance.Rules.UseSharedSpeed
            ? SharedSpeed
            : -1;

        EmpPop.Information("emp_server_started".lang());

        CardCache.CacheCurrentZone();

        StartWorldStateUpdate();
    }

    protected override void RegisterPackets()
    {
        Router.RegisterHandler<SessionNewPlayerResponse>(OnSessionNewPlayerResponse);
        Router.RegisterHandler<MapDataRequest>(OnMapDataRequest);
        Router.RegisterHandler<ZoneDataReceivedResponse>(OnZoneDataReceivedResponse);
        Router.RegisterHandler<WorldStateRequest>(OnWorldStateRequest);
        Router.RegisterHandler<WorldStateDeltaList>(OnWorldStateDeltaResponse);
        Router.RegisterHandler<CharaStateSnapshot>(OnClientRemoteCharaSnapshot);

        // source validation
        Router.RegisterHandler<SourceValidationResponse>(OnSourceValidationResponse);
        Router.RegisterHandler<SourceValidationContinue>(OnSourceValidationContinue);
    }

    private void Broadcast<T>(T packet)
    {
        Socket.Broadcast.Send(packet);
    }

    protected void DisconnectInactive()
    {
        foreach (var peer in Socket.Peers) {
            if (!States.TryGetValue(peer.Id, out var state)) {
                continue;
            }

            if (state.LastReceivedTick == -1) {
                continue;
            }

            // client has not been responding after 25 ticks
            if (!peer.IsConnected && Session.Tick - state.LastReceivedTick > 25) {
                Socket.Disconnect(peer, EmpDisconnectInfo.InactivePeer);
            }
        }

        // remove all left over chara
        foreach (var chara in _map.charas.ToArray()) {
            if (chara.GetBool("remote_chara") && !ActiveRemoteCharas.Values.Contains(chara)) {
                RemoveRemoteChara(chara);
            }
        }
    }

#region Net Events

    protected override void OnPeerConnected(ISteamNetPeer peer)
    {
        var sw = Stopwatch.StartNew();
        while (peer.User.Name is null && sw.ElapsedMilliseconds <= 500) {
            // do a spin wait to pin the username
        }

        EmpPop.Information("emp_player_connected".lang(), peer);

        // do source validations
        RequestSourceValidation(peer);

        // and invite to steam lobby if clients aren't already in
        peer.Send(new SteamLobbyRequest {
            LobbyId = Session.Lobby.Current,
        });

#if DEBUG
        DebugProgress ??= EGui.CreatePopup(() => new(BuildDebugInfo()), _ => false, 1f);
#endif
    }

    protected override void OnPeerDisconnected(ISteamNetPeer peer, string disconnectInfo)
    {
        EmpPop.Information("emp_player_disconnected".lang(), peer, disconnectInfo);

        if (States.Remove(peer.Id, out var state)) {
            // Fully remove remote chara from the map (saved chara remains via ElinGameIOProperty)
            if (ActiveRemoteCharas.Remove(peer.Id, out var remoteChara)) {
                RemoveRemoteChara(remoteChara);
                EmpLog.Information("Player {Name} remote chara {Uid} removed from map. " +
                                   "Saved chara retained for future new connections.",
                    state.User.Name, remoteChara.uid);
            }

            Session.CurrentPlayers.Remove(state);
        }

        EmpLog.Debug("Player {Name} disconnected. {Remaining} players remaining",
            state?.User.Name ?? "unknown", States.Count);

        Broadcast(SessionPlayersSnapshot.Create());

        // keep ticking but no update
        if (States.Count == 0) {
            PauseWorldStateUpdate();
            DebugProgress?.Kill();
        }
    }

#endregion
}