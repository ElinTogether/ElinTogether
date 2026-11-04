using System;
using System.Collections.Generic;
using System.Linq;
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
    private const int MaxDeltasPerBatch = 2048;

    private readonly WorldStateSnapshot.ChangeTracker _snapshotChanges = new();

    /// <summary>
    ///     Propagate current world state snapshot
    /// </summary>
    /// <param name="toEveryone">The regular one: only what changed since the last regular one, see WorldStateSnapshot.Partial</param>
    public WorldStateSnapshot PropagateWorldState(bool toEveryone = false)
    {
        return core.game?.activeZone?.map is null
            ? _lastTick!
            : WorldStateSnapshot.Create(toEveryone ? _snapshotChanges : null);
    }

    /// <summary>
    ///     If the server disabled shared speed, we use -1
    /// </summary>
    private void RefreshSharedSpeed()
    {
        Session.SharedSpeed = NetSession.Instance.Rules.UseSharedSpeed
            ? SharedSpeed
            : -1;
    }

    /// <summary>
    ///     Send out world state for client-side reconciliation
    /// </summary>
    private void WorldStateSnapshotUpdate()
    {
        Session.Tick++;
        Session.CurrentZone = _zone;
        RefreshSharedSpeed();

        // host box "Large crowds": every character in this one, each player then sent its own part (ElinNetHostCrowd)
        var crowd = Crowds && core.game?.activeZone?.map is not null;
        try {
            _lastTick = crowd ? WorldStateSnapshot.Create() : PropagateWorldState(true);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Exception at server tick update");
        }

        if (_lastTick is null) {
            EmpLog.Warning("WorldStateSnapshot is null at tick {Tick}, skipping broadcast", Session.Tick);
            return;
        }

        var tick = _lastTick;
        if (crowd) {
            try {
                SendCrowdSnapshot(tick);
                return;
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Exception at the crowd snapshot of tick {Tick}, sent whole", Session.Tick);
                _crowdSent.Clear();
            }
        }

        SendByMap(tick, () => tick.Lean());
    }

    // a player who just left still takes the last results of its stay (DepartFromHostMap sends them after marking it)
    private const float OffMapGraceSeconds = 2f;

    /// <summary>
    ///     Interest by map (council 2026-10-27): a player away from the host map, whose game drops what is about
    ///     that map (ElinNetClient.ShouldReceiveWhileAway, ReachesAwayPlayer). A zone session sends as before
    /// </summary>
    internal bool IsOffHostMap(ISteamNetPeer peer)
    {
        return !IsZoneSession && _departed.Contains(peer.Id) &&
               UnityEngine.Time.realtimeSinceStartup - _departedAt.GetValueOrDefault(peer.Id) > OffMapGraceSeconds;
    }

    /// <summary>
    ///     A player past its handshake (the host accepted it). Before that its game drops snapshots, deltas and the
    ///     player list (ElinNetClient.ShouldReceiveHostPacket), so they are not sent to it at all
    /// </summary>
    private bool IsPlaying(ISteamNetPeer peer)
    {
        return _handshakes.TryGetValue(peer.Id, out var state) && state.Phase == NetHandshakePhase.Joined;
    }

    /// <summary>
    ///     To every playing player, as Broadcast, except that a player away from the host map gets the other packet
    ///     (none when null). Each one serialized once; the same reliable link per player, so the order does not change.
    ///     A player still in its handshake gets nothing (see IsPlaying; the session rules, which it reads then, are
    ///     sent by Broadcast, not here)
    /// </summary>
    private void SendByMap<T>(T onMap, Func<T?> offMap) where T : class
    {
        var peers = Socket.Peers;
        var everyone = true;
        for (var i = 0; i < peers.Count && everyone; i++) {
            everyone = IsPlaying(peers[i]) && !IsOffHostMap(peers[i]);
        }

        if (everyone) {
            Broadcast(onMap);
            return;
        }

        byte[]? full = null;
        byte[]? lean = null;
        var leanBuilt = false;
        foreach (var peer in peers) {
            if (!IsPlaying(peer)) {
                continue;
            }

            if (IsOffHostMap(peer)) {
                if (!leanBuilt) {
                    leanBuilt = true;
                    lean = offMap() is { } packet ? Socket.Serializer.Serialize(packet) : null;
                }

                if (lean is not null) {
                    peer.Send(lean);
                }
            } else {
                peer.Send(full ??= Socket.Serializer.Serialize(onMap));
            }
        }
    }

    /// <summary>
    ///     Send out local deltas to remote clients
    /// </summary>
    internal void WorldStateDeltaUpdate()
    {
        GateItems.TellGateZone(this);

        if (!Delta.HasPendingOut) {
            return;
        }

        if (Delta.FlushOutBuffer() is not { Count: > 0 } deltaList) {
            return;
        }

#if DEBUG
        NetMetrics.SentDeltas(deltaList);
#endif

        if (Crowds && _crowdViews.Count > 0) {
            SendCrowdDeltas(deltaList);
            return;
        }

        SendByMap(new WorldStateDeltaList {
                DeltaList = deltaList,
            },
            () => deltaList.Where(ElinNetClient.ReachesAwayPlayer).ToList() is { Count: > 0 } kept
                ? new WorldStateDeltaList { DeltaList = kept }
                : null);
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
        // a player still loading the world it was sent is not on this map yet either: what it says about cards
        // is about the world it is leaving, and would land after the copy it was sent (an item dropped then
        // exists twice)
        var away = IsAway(peer) || !_settled.Contains(peer.Id);
        var count = 0;
        foreach (var delta in response.DeltaList) {
            // a buggy client may send a null or a huge batch: drop them rather than throw for the whole batch
            if (delta is null) {
                EmpLog.Debug("Null delta from player {@Peer}, skipped", peer);
                continue;
            }

            if (++count > MaxDeltasPerBatch) {
                EmpLog.Warning("Delta batch from player {@Peer} cut at {Max} of {Total}", peer, MaxDeltasPerBatch, response.DeltaList.Count);
                break;
            }

            // an away player is not on the host map, only what it shares with the world is relevant here:
            // its chat and the quest log (and its world-map steps, held as its lead in Personal Pace: dropped here,
            // a guest's lead never grew, bench personalpace_suite 2026-10-23); with affinity per player, what its own
            // acts there did to a resident's affinity with it
            if (away && !OwnGrowth(delta, peer) && delta is not (MsgSayDelta or WhisperDelta or FollowTravelDelta or GateItemRequest or MarriageDelta or QuestStartDelta or QuestCompleteDelta or QuestChangePhaseDelta or DialogFlagDelta or CardModCurrencyDelta { Amount: < 0 } or StoryOutcomeDelta or NymelleFarrisDelta or QuestFailDelta or QuestUpdateDelta or PersonalQuestDelta or PlayerStandingDelta or BillPayDelta or WorldTimeReportDelta or QuestFollowDelta or CharaSleepDelta or SleepStateDelta or PlayerIdleDelta or CodexDelta or AddRecipeDelta or BossWinDelta or ExtraTaxDelta or TentZoneDelta or WorldTravelStepDelta { Away: true } or AdventurerBoutDelta or BaseRelocateDelta or SiblingLandDelta or ClaimZoneDelta or MailRequestDelta
                    // (the Multiplayer menu's asks: a team, the market, the boards, a teammate's sheet, the host's log for a
                    // bug report; dropped before, so a player on its own map could not found a team, user 2026-10-30)
                    or TeamRequestDelta or MarketAskDelta or LeaderboardDelta or JournalDelta or CharaSheetDelta or HostLogRequest
                    or TeamQuestUpdateDelta or TeamQuestDoneDelta or TeamRankRequestDelta
                    // (a dead player revived from a bartender of its own map, paid there: GitHub issue 59)
                    or CharaReviveRequestDelta { PaidAway: true }) &&
                !(delta is CharaAffinityDelta && Helper.AffinityPerPlayer.On)) {
                continue;
            }

            // a flood of one kind of delta (a broken or modified game): dropped past a generous budget
            if (Floods(delta, peer.Id)) {
                continue;
            }

            delta.OriginPeer = peer.Id;
            delta.FromPeer = true;
            Delta.AddLocal(delta);
        }
    }

    /// <summary>
    ///     What a player's own character learnt (a skill, an attribute, its level) is its own game's to say,
    ///     wherever it is: skipped while it was away or arriving, this copy kept the old value until the same
    ///     skill moved again, and gave it back at the next world copy (attributes that "do not go up")
    /// </summary>
    private bool OwnGrowth(ElinDelta delta, ISteamNetPeer peer)
    {
        // and the food its world-map auto-eat spent from its own bag or a companion's (AutoEatPatch): the host's
        // copy of the stack is the one that counts, whichever map the host is on
        if (delta is CardModNumDelta num) {
            return num.Card.Find()?.GetRootCard() is Chara holder &&
                   (ActiveRemoteCharas.GetValueOrDefault(peer.Id) ?? KeptChara(peer)) is { } self &&
                   ElinDelta.OwnsOrCompanion(self, holder);
        }

        var owner = delta switch {
            ElementChangeDelta element => element.Owner,
            CharaLevelDelta level => level.Owner,
            _ => null,
        };
        return owner is not null && IsOwnChara(peer, owner.Uid);
    }

    /// <summary>
    ///     That character is the one this player plays: on this map, or kept for it while it is away or arriving
    /// </summary>
    /// <summary>
    ///     The character that player plays, on this map or kept for it while it is away
    /// </summary>
    internal Chara? PlayerChara(int peerId)
    {
        return ActiveRemoteCharas.GetValueOrDefault(peerId) ??
               (Socket.Peers.FirstOrDefault(p => p.Id == peerId) is { } peer ? KeptChara(peer) : null);
    }

    internal bool IsOwnChara(int peerId, int uid)
    {
        return IsOwnChara(Socket.Peers.FirstOrDefault(p => p.Id == peerId), uid);
    }

    private bool IsOwnChara(ISteamNetPeer? peer, int uid)
    {
        return peer is not null && (ActiveRemoteCharas.GetValueOrDefault(peer.Id) ?? KeptChara(peer))?.uid == uid;
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
        state.Turbo = response.State.Turbo;
        state.Talking = response.State.Talking;
        state.LastReceivedTick = response.State.LastReceivedTick;

        var chara = ActiveRemoteCharas[peer.Id];
        response.ApplyReconciliation(chara);

        HaltAbandonedAct(state, chara);
    }

    internal void SweepStaleCellEntries()
    {
        if (_map?.cells is not { } cells) {
            return;
        }

        // (one lookup per character standing on a tile: RemoteCharaMap keeps its characters in a set)
        var remotes = ActiveRemoteCharas;
        var size = _map.Size;
        for (var x = 0; x < size; ++x) {
            for (var z = 0; z < size; ++z) {
                if (cells[x, z].detail?.charas is not { Count: > 0 } list) {
                    continue;
                }

                for (var i = list.Count - 1; i >= 0; --i) {
                    var c = list[i];
                    if (!remotes.ContainsValue(c)) {
                        continue;
                    }

                    // a player standing here, listed once: kept. Only its second entry, or an entry of a tile it left,
                    // goes (two players on one tile counted as one doubled player: the second vanished from the
                    // tile at every sweep, 53k removals in the 200-guest load test of 2026-11-03)
                    if (c.pos.x == x && c.pos.z == z && list.IndexOf(c) == i) {
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
        /**
        foreach (var chara in ActiveRemoteCharas.Values) {
            EmpLog.Debug("Remote chara {Uid} ai {ActType} at {@Pos} (in map: {InActiveMap})",
                chara.uid, chara.ai?.GetType().Name, chara.pos,
                chara.IsInActiveMap && (_map?.charas.Contains(chara) ?? false));
        }
        /**/

        SweepStaleCellEntries();
        KeepFrozen();

        // collect peer connection stats into player states
        foreach (var peer in Socket.Peers) {
            if (States.TryGetValue(peer.Id, out var state)) {
                state.Idle = PlayerIdle.IsIdle(peer.Id);
                state.LastPingMs = peer.Stat.LastPingMs;
                state.AvgPingMs = peer.Stat.AvgPingMs;
                state.ConnectionQualityLocal = peer.Stat.ConnectionQualityLocal;
                state.ConnectionQualityRemote = peer.Stat.ConnectionQualityRemote;
            }
        }

        if (States.TryGetValue(0, out var self)) {
            self.Idle = PlayerIdle.IsIdle(0);
        }

        SeePlayers();

        if (Crowds) {
            SendPlayersOnChange(SessionPlayersSnapshot.Create());
        } else {
            SendByMap(SessionPlayersSnapshot.Create(), () => null);
        }
    }

#region Scheduler Jobs

    /// <summary>
    ///     Subscribe all scheduler jobs
    ///     TODO profile the snapshot cost, see if we can use more granular hashed snapshot
    /// </summary>
    public void StartWorldStateUpdate()
    {
        // 0.5hz session player states update
        Scheduler.Subscribe(UpdateRemotePlayerStates, 0.5f);
        // 1hz roster of the whole server, for the menus (sent on a change, see ElinNetHostRoster)
        Scheduler.Subscribe(UpdateServerRoster, 1f);
        // 5hz world snapshot reconciliation
        Scheduler.Subscribe(WorldStateSnapshotUpdate, 5f);
        // 50hz delta dispatch
        Scheduler.Subscribe(SynchronizationContext.AllowDeltaSending, 50f);
    }

    /// <summary>
    ///     Unsubscribe all scheduler jobs, also resets the server tick
    /// </summary>
    public void StopWorldStateUpdate()
    {
        Scheduler.Unsubscribe(UpdateRemotePlayerStates);
        Scheduler.Unsubscribe(WorldStateSnapshotUpdate);
        Scheduler.Unsubscribe(SynchronizationContext.AllowDeltaSending);

        Session.Tick = 0;

        EmpLog.Debug("Stopping server state update");
    }

#endregion
}