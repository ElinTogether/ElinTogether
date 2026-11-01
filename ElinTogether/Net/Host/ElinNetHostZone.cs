using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using ElinTogether.Patches;
using Serilog.Context;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    /// <summary>
    ///     Player -> the loaded map it was last put on by OnZoneDataReceivedResponse (weak: an unloaded map is let go)
    /// </summary>
    private readonly Dictionary<ulong, WeakReference<Map>> _placedOn = [];

    /// <summary>
    ///     Send the player a map snapshot from given zone
    /// </summary>
    public void PropagateZoneChangeState(Zone zone, ISteamNetPeer? peer = null)
    {
        using var _ = LogContext.PushProperty("Zone", new { zone.ZoneFullName, ZoneUid = zone.uid }, true);

        EmpPop.Debug("emp_zone_change".lang());

        // nobody connected (a guest away is still a peer): saving and compressing the map is for nothing, whoever
        // comes later asks for the zone (OnMapDataRequest)
        if (peer is null && Socket.Peers.Count == 0) {
            Session.Lobby.Current[EmpLobbyData.CurrentZone] = zone.NameWithLevel;
            return;
        }

        // council 11: the players who stay on this very map keep their copy of it, see CompleteSoftRejoin
        if (peer is null && TakeSoftRejoins(zone) is { Count: > 0 } staying) {
            // what is already done is in the map and in the numbers sent below: it goes out before them
            Delta.RefreshBuffer();
            WorldStateDeltaUpdate();

            var others = Socket.Peers.Where(p => !staying.Contains(p)).ToList();
            if (others.Count > 0) {
                var map = ZoneDataResponse.Create(zone);
                foreach (var other in others) {
                    other.Send(map);
                }
            }

            foreach (var stays in staying) {
                CompleteSoftRejoin(stays, zone);
            }

            InviteToQuestZone(zone);
            Session.Lobby.Current[EmpLobbyData.CurrentZone] = zone.NameWithLevel;
            return;
        }

        // what is already done on this map is in the copy below: it goes out before the copy, not after it
        // (it was played a second time on top of the map)
        Delta.RefreshBuffer();
        WorldStateDeltaUpdate();

        var packet = ZoneDataResponse.Create(zone);

        if (peer is not null) {
            EmpLog.Debug("Dispatching zone to player {@Peer}",
                peer);

            peer.Send(packet);
        } else {
            EmpLog.Debug("Dispatching zone to all players");

            Broadcast(packet);

            // after the zone change: the question is only asked while the host is known to be there
            InviteToQuestZone(zone);
        }

        // update lobby data
        Session.Lobby.Current[EmpLobbyData.CurrentZone] = zone.NameWithLevel;
    }

    /// <summary>
    ///     Net event: Send the clients a map snapshot
    /// </summary>
    private void OnMapDataRequest(MapDataRequest request, ISteamNetPeer peer)
    {
        using var _ = LogContext.PushProperty("Zone", request, true);

        EmpLog.Information("Received zone state request from player {@Peer}",
            peer);

        var zone = _zone;
        if (request.ZoneUid != -1) {
            zone = game.spatials.Find(request.ZoneUid) ??
                   ModUtil.FindZoneByFullName(request.ZoneFullName);
        }

        if (zone is not null) {
            // as in SendSaveProbe: what is already done is in the copy, it goes out before it
            Delta.RefreshBuffer();
            WorldStateDeltaUpdate();

            PropagateZoneChangeState(zone, peer);
        } else {
            EmpLog.Warning("Player {@Peer} requested invalid zone state",
                peer);

            // gtfo
            Socket.Disconnect(peer, EmpDisconnectInfo.InvalidZone);
        }
    }

    /// <summary>
    ///     Net event: Clients have replicated the zone and map, ready to transition
    /// </summary>
    private void OnZoneDataReceivedResponse(ZoneDataReceivedResponse response, ISteamNetPeer peer)
    {
        EmpLog.Debug("Player {@Peer} has finished zone replication",
            peer);

        if (!ActiveRemoteCharas.TryGetValue(peer.Id, out var chara)) {
            EmpLog.Debug("Player {@Peer} has no registered chara yet, ignoring",
                peer);
            return;
        }

        // check if the received zone is still the current zone
        // in case clients have a high RTT and host fast fingered to another zone
        if (response.ZoneUid != _zone.uid) {
            EmpLog.Debug("...but the zone state is stale, switching to new zone state {@Zone}",
                new {
                    _zone.ZoneFullName,
                    ZoneUid = _zone.uid,
                });

            PropagateZoneChangeState(_zone, peer);
            return;
        }

        // we only move their characters to zone when they are ready
        Delta.AddRemote(CardGenDelta.Create(chara));

        // move instead of add. Next to us only when nothing tells where (it joins the game), and only when we
        // stand on this map: waking up, the game may show a base our character never entered (its walk through
        // the bases, Player.SimulateFaction, stopped by a recall), and our tile is the world map's then
        Point? pos = pc.currentZone != _zone
            ? null
            : _zone.IsRegion
                ? WorldMapTileNextToUs(chara)
                : pc.pos.GetNearestPoint(allowChara: false, allowInstalled: false) ?? pc.pos.Copy();

        // it was on this map all along (the one simulating it came or went, or the player dropped): it stands
        // where it stood, with its companions. Or it walks in: it arrives where the game puts a player alone
        var keepSpot = false;
        if (_returnSpots.Remove(peer.User, out var spot) && spot.ZoneUid == _zone.uid &&
            UnityEngine.Time.unscaledTime < spot.Until) {
            var at = spot.Arrival is { } arrival ? ArrivalPoint(chara, arrival) : spot.Pos;
            if (at is { IsInBounds: true }) {
                keepSpot = spot.Arrival is null && !spot.Stale;
                // that very tile when it is free: it may hold something to stand on (stairs, a bed)
                // its own tile is not taken by someone else: a map reloaded in place must not push it one tile away
                pos = _zone.IsRegion || (chara.pos.x == at.x && chara.pos.z == at.z)
                    ? at.Copy()
                    : at.GetNearestPoint(allowChara: false) ?? at.Copy();
            }
        } else if (_placedOn.TryGetValue(peer.User, out var placed) && placed.TryGetTarget(out var placedMap) &&
                   placedMap == _map && chara.IsInActiveMap && placedMap.charas.Contains(chara) &&
                   chara.pos is { IsValid: true, IsInBounds: true }) {
            // GitHub issue 52: this map was already sent to it and it was put on it (at its own tile, the spot
            // above is spent), and the same map comes again (the host's own zone change after the copy sent to
            // that player alone, after the host stepped out and back in): it stays where it stands instead of
            // being put next to the host
            pos = chara.pos.Copy();
            keepSpot = true;
        }
        // never a tile outside this map: the game does not check (Map.OnCardAddedToZone), the character would
        // be left in the list of the map and on no tile, and its player without an answer
        if (pos is not { IsValid: true, IsInBounds: true }) {
            EmpLog.Warning("No tile of {ZoneFullName} for player {@Peer} ({@Pos}, host in {HostZone}), it stands at the entrance",
                _zone.ZoneFullName, peer, pos, pc.currentZone?.ZoneFullName);

            keepSpot = false;
            pos = EntrancePoint(chara);
        }

        try {
            if (chara.IsInActiveMap && _map.charas.Contains(chara)) {
                if (chara.Stub_Move(pos, Card.MoveType.Force) != Card.MoveResult.Success) {
                    pos = chara.pos.Copy();
                }
            } else {
                // its tile on the map it comes from may not exist here
                if (!chara.pos.IsValid || !chara.pos.IsInBounds) {
                    chara.pos.Set(pos.x, pos.z);
                }

                _zone.AddCard(chara, pos);
            }
        } catch (Exception ex) {
            EmpLog.Error(ex, "Player {@Peer} could not be put at {@Pos} in {ZoneFullName}, trying the entrance",
                peer, pos, _zone.ZoneFullName);

            pos = EntrancePoint(chara);
            try {
                chara.pos.Set(pos.x, pos.z);
                if (_map.charas.Contains(chara)) {
                    _zone.RemoveCard(chara);
                }

                _zone.AddCard(chara, pos);
            } catch (Exception again) {
                EmpLog.Error(again, "Player {@Peer} is not on {ZoneFullName}", peer, _zone.ZoneFullName);
            }
        }

        try {
            if (chara.ai is not GoalRemote) {
                chara.SetAI(GoalRemote.Default);
            }

            BringCompanions(chara, keepSpot);
            RestoreRideTies(peer.User);
            // sales made while it was a guest somewhere or offline
            PayShipping(chara.uid);
            // what the story gave before this player was of the world
            CatchUpStoryShares(chara);
            SweepStaleCellEntries();
        } catch (Exception ex) {
            // the player is answered whatever happens here: without the answer it waits on its loading screen
            EmpLog.Error(ex, "Arrival of player {@Peer} in {ZoneFullName} left unfinished", peer, _zone.ZoneFullName);
        }

        EmpLog.Debug("Assigned zone sync position to player {@Peer} at {@Pos}",
            peer, pos);
        _placedOn[peer.User] = new(_map);

        // after that, their characters will always be party members
        peer.Send(new ZoneActivateResponse {
            ZoneUid = _zone.uid,
            Pos = pos,
        });

        MarkSettled(peer);
        SendPersonalState(peer, chara.uid);

        // the guards of this map may be after that one
        if (IsCriminal(chara)) {
            _zone.RefreshCriminal();
        }

        RemoveLeftOverCharas(null);
    }

    /// <summary>
    ///     Where a player stands when no tile of this map is known for it: where the game puts someone who comes
    ///     in with no way in (Zone.GetSpawnPos: the guide spot of a base, the embark tile), else the middle.
    ///     Always a tile of this map
    /// </summary>
    private static Point EntrancePoint(Chara chara)
    {
        Point? pos = null;
        if (!_zone.IsRegion && chara.global is { } data) {
            try {
                // not the way it took into another map
                data.transition = null;
                pos = _zone.GetSpawnPos(chara, ZoneTransition.EnterState.Center);
            } catch (Exception ex) {
                EmpLog.Warning(ex, "No entrance for chara {Uid} in {ZoneFullName}", chara.uid, _zone.ZoneFullName);
            } finally {
                data.transition = null;
            }
        }

        if (pos is not { IsValid: true, IsInBounds: true }) {
            var middle = _map.bounds.GetCenterPos();
            pos = middle.GetNearestPoint(allowChara: false) ?? middle;
        }

        return pos.Clamp(useBounds: true);
    }

    /// <summary>
    ///     As the game places the party on the world map (Zone.AddGlobalCharasOnActivate): a tile next to ours
    ///     it can walk to. The nearest free tile alone may be the sea: a tile that blocks is refused as the game
    ///     refuses the step (Chara.CanMoveTo), and so is water (EloMap.IsWater). Ours when there is none
    /// </summary>
    private static Point WorldMapTileNextToUs(Chara chara)
    {
        var pos = pc.pos.Copy();
        var elomap = scene.elomap;

        pc.pos.ForeachNearestPoint(p => {
            if (elomap.IsWater(p.x + elomap.minX, p.z + elomap.minY) ||
                !PathManager.Instance.IsPathClear(pc.pos, p, chara, 10)) {
                return false;
            }

            pos = p.Copy();
            return true;
        }, allowBlock: false, allowChara: false, allowInstalled: true, ignoreCenter: true, maxRange: 2);

        return pos;
    }

    /// <summary>
    ///     Where the game puts a player walking into this map alone: the stairs, the gate or the edge it comes by
    ///     (Zone.GetSpawnPos), on the world map the tile of the place it comes out of. Null when nothing tells
    /// </summary>
    private static Point? ArrivalPoint(Chara chara, ZoneArrival arrival)
    {
        if (_zone.IsRegion) {
            // that tile itself, as the game does: a neighbour of a harbour is the sea
            var top = game.spatials.Find(arrival.LastZoneUid)?.GetTopZone();
            return top is not null && _zone.GetZoneAt(top.x, top.y) is not null
                ? new Point(top.mapX, top.mapY)
                : null;
        }

        if (chara.global is not { } data) {
            return null;
        }

        try {
            data.transition = arrival.ToTransition();
            return _zone.GetSpawnPos(chara)?.Clamp(useBounds: true);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "No arrival point for chara {Uid} in {ZoneFullName}", chara.uid, _zone.ZoneFullName);
            return null;
        } finally {
            data.transition = null;
        }
    }
}