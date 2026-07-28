using System;
using System.Collections.Generic;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using ElinTogether.Patches;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    private readonly Dictionary<int, int> _idleReports = [];
    private WorldStateSnapshot? _lastTick;
    private bool _pauseUpdate;

    /// <summary>
    ///     Propagate current world state snapshot
    /// </summary>
    public WorldStateSnapshot PropagateWorldState()
    {
        return core.game?.activeZone?.map is null
            ? _lastTick!
            : WorldStateSnapshot.Create();
    }

    /// <summary>
    ///     Send out world state for client-side reconciliation
    /// </summary>
    private void WorldStateSnapshotUpdate()
    {
        if (_pauseUpdate) {
            return;
        }

        Session.Tick++;
        Session.CurrentZone = _zone;

        try {
            _lastTick = PropagateWorldState();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Exception at server tick update");
        }

        if (_lastTick is null) {
            EmpLog.Warning("WorldStateSnapshot is null at tick {Tick}, skipping broadcast", Session.Tick);
            return;
        }

        Broadcast(_lastTick);
    }

    /// <summary>
    ///     Send out local deltas to remote clients
    /// </summary>
    internal void WorldStateDeltaUpdate()
    {
        if (_pauseUpdate) {
            return;
        }

        if (!Delta.HasPendingOut) {
            return;
        }

        if (Delta.FlushOutBuffer() is not { Count: > 0 } deltaList) {
            return;
        }

        Broadcast(new WorldStateDeltaList {
            DeltaList = deltaList,
        });
    }

    /// <summary>
    ///     Process local or deferred deltas received from clients
    /// </summary>
    internal void WorldStateDeltaProcess()
    {
        if (!Delta.HasPendingIn) {
            return;
        }

        Delta.ProcessLocalBatch(this);
    }

    /// <summary>
    ///     Update remote client's session rules
    /// </summary>
    internal void UpdateRemoteSessionRules()
    {
        NetSession.Instance.Rules = NetSessionRules.Default;
        Broadcast(NetSession.Instance.Rules);
    }

    /// <summary>
    ///     Net event: Respond to manual requests
    /// </summary>
    private void OnWorldStateRequest(WorldStateRequest request, ISteamNetPeer peer)
    {
        peer.Send(PropagateWorldState());
    }

    /// <summary>
    ///     Net event: Apply delta changes from all clients
    /// </summary>
    private void OnWorldStateDeltaResponse(WorldStateDeltaList response, ISteamNetPeer peer)
    {
        foreach (var delta in response.DeltaList) {
            delta.OriginPeer = peer.Id;
            Delta.AddLocal(delta);
        }
    }

    /// <summary>
    ///     Net event: Check remote character's snapshot
    /// </summary>
    private void OnClientRemoteCharaSnapshot(CharaStateSnapshot response, ISteamNetPeer peer)
    {
        if (!States.TryGetValue(peer.Id, out var state)) {
            EmpLog.Warning("Received invalid remote character from player {@Peer}",
                peer);
            return;
        }

        if (response.State is null) {
            EmpLog.Warning("Received empty remote character state from player {@Peer}",
                peer);
            return;
        }

        state.LastAct = response.State.LastAct;
        state.Speed = response.State.Speed;
        state.LastReceivedTick = response.State.LastReceivedTick;

        // if server disabled shared speed, we use -1
        Session.SharedSpeed = NetSession.Instance.Rules.UseSharedSpeed
            ? SharedSpeed
            : -1;

        WorldStateSnapshot.CachedRemoteSnapshots.Add(response);

        var chara = ActiveRemoteCharas[peer.Id];
        response.ApplyReconciliation(chara);

        HaltAbandonedAct(state, chara);
    }

    internal void SweepStaleCellEntries()
    {
        if (_map?.cells is not { } cells) {
            return;
        }

        var size = _map.Size;
        for (var x = 0; x < size; ++x) {
            for (var z = 0; z < size; ++z) {
                if (cells[x, z].detail?.charas is not { Count: > 0 } list) {
                    continue;
                }

                var seen = 0;
                for (var i = list.Count - 1; i >= 0; --i) {
                    var c = list[i];
                    if (!ActiveRemoteCharas.ContainsValue(c)) {
                        continue;
                    }

                    if (c.pos.x == x && c.pos.z == z && ++seen == 1) {
                        continue;
                    }

                    EmpLog.Warning("Removing stale chara {Uid} at {@FromPos}, actual pos {@Pos}",
                        c.uid, new Point(x, z), c.pos);
                    list.RemoveAt(i);
                }
            }
        }

        foreach (var chara in ActiveRemoteCharas.Values) {
            var first = _map.charas.IndexOf(chara);
            if (first < 0) {
                continue;
            }

            for (var i = _map.charas.Count - 1; i > first; --i) {
                if (_map.charas[i] != chara) {
                    continue;
                }

                EmpLog.Warning("Removing duplicate map chara {Uid} at {@Pos}",
                    chara.uid, chara.pos);
                _map.charas.RemoveAt(i);
            }
        }
    }

    private void HaltAbandonedAct(NetPeerState state, Chara chara)
    {
        if (chara.ai is GoalRemote { child: { } stuck } &&
            ActMappingValidator.Default.IdToActMapping.TryGetValue(state.LastAct, out var reported) &&
            typeof(NoGoal).IsAssignableFrom(reported)) {
            if ((_idleReports[state.Index] = _idleReports.GetValueOrDefault(state.Index) + 1) < 3) {
                return;
            }

            EmpLog.Debug("Halting abandoned act {ActType} of remote chara {Uid}, client reports idle",
                stuck.GetType().Name, chara.uid);

            if (stuck is NoGoal) {
                ((GoalRemote)chara.ai).HaltChildAct();
            } else {
                TaskCache.RequestCancel(this, chara, stuck);
            }
        }

        _idleReports[state.Index] = 0;
    }

    private void UpdateRemotePlayerStates()
    {
        foreach (var chara in ActiveRemoteCharas.Values) {
            EmpLog.Debug("Remote chara {Uid} ai {ActType} at {@Pos} (in map: {InActiveMap})",
                chara.uid, chara.ai?.GetType().Name, chara.pos,
                chara.IsInActiveMap && (_map?.charas.Contains(chara) ?? false));
        }

        SweepStaleCellEntries();

        // collect peer connection stats into player states
        foreach (var peer in Socket.Peers) {
            if (States.TryGetValue(peer.Id, out var state)) {
                state.LastPingMs = peer.Stat.LastPingMs;
                state.AvgPingMs = peer.Stat.AvgPingMs;
                state.ConnectionQualityLocal = peer.Stat.ConnectionQualityLocal;
                state.ConnectionQualityRemote = peer.Stat.ConnectionQualityRemote;
            }
        }

        Broadcast(SessionPlayersSnapshot.Create());
    }

#region Scheduler Jobs

    /// <summary>
    ///     Subscribe all scheduler jobs and reset pause state
    ///     TODO profile the snapshot cost, see if we can use more granular hashed snapshot
    /// </summary>
    public void StartWorldStateUpdate()
    {
        // 0.5hz session player states update
        Scheduler.Subscribe(UpdateRemotePlayerStates, 0.5f);
        // 5hz world snapshot reconciliation
        Scheduler.Subscribe(WorldStateSnapshotUpdate, 5f);
        // 50hz delta dispatch
        Scheduler.Subscribe(SynchronizationContext.AllowDeltaSending, 50f);

        _pauseUpdate = false;
    }

    /// <summary>
    ///     Unsubscribe all scheduler jobs and reset pause state, also resets the server tick
    /// </summary>
    public void StopWorldStateUpdate()
    {
        Scheduler.Unsubscribe(UpdateRemotePlayerStates);
        Scheduler.Unsubscribe(WorldStateSnapshotUpdate);
        Scheduler.Unsubscribe(SynchronizationContext.AllowDeltaSending);

        Session.Tick = 0;

        _pauseUpdate = false;

        EmpLog.Debug("Stopping server state update");
    }

    /// <summary>
    ///     Pause sending out deltas, *but they still accumulate*
    /// </summary>
    public void PauseWorldStateUpdate()
    {
        _pauseUpdate = true;

        EmpLog.Debug("Pausing server state update");
    }

    /// <summary>
    ///     Resume sending out deltas
    /// </summary>
    public void ResumeWorldStateUpdate(bool clearDelta)
    {
        _pauseUpdate = false;

        EmpLog.Debug("Resuming server state update");

        if (clearDelta) {
            Delta.ClearOut();
        }
    }

#endregion
}