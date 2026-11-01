using ElinTogether.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Council 11, host rule SoftRecall: the host walks onto the map a player holds alone. That player hands the
///     map and its character back as always, but keeps its game and its scene: no copy of the world, no map sent.
///     The mirror of LeavePlayersBehind / ElinNetClient.TakeOverZone. <br />
///     Release (Soft) -> the host applies it, registers the player (RegisterSoftRejoin), walks in and loads the
///     map -> where it tells everyone its map (PropagateZoneChangeState) that player gets
///     <see cref="ZoneSoftRejoin" /> instead (CompleteSoftRejoin) and is placed on its tile. Anything unexpected on
///     either side ends with the copy of the world, as without the rule (FallBackToWorldCopy) <br />
///     Slice 2, same rule: the player walks into the map the host stands on. Release (Soft, Arrival) -> the host
///     applies it, registers the player, tells it <see cref="ZoneSoftRejoin" /> (Travel) and sends that one map
///     (SendSoftReturn): the player keeps its game and loads the map as a client following the host does
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     How long the host may take to stand on the map (a save, the load): longer than the player waits before
    ///     it asks for the world itself, see ElinNetClient.SoftRejoinWaitSeconds
    /// </summary>
    private const float SoftEnterSeconds = 30f;

    /// <summary>
    ///     After ZoneSoftRejoin: how long the player may still say it did not work
    /// </summary>
    private const float SoftFailSeconds = 30f;

    /// <summary>
    ///     Peer id -> the map it stays on, the numbers given to its cards that waited for one, until when, and
    ///     whether the host stands there and said so (ZoneSoftRejoin sent)
    /// </summary>
    private readonly Dictionary<int, (int ZoneUid, Dictionary<int, int> Rebinds, float Deadline, bool Entered)> _softRejoins = [];

    /// <summary>
    ///     The player holding that zone may stay on it when the host comes: no visitor on its way there (those
    ///     standing there stay too, each the same way, GitHub issue 46), and it is not the zone of a quest (gone
    ///     once left) nor the world map (never recalled)
    /// </summary>
    private bool CanRecallSoftly(Zone zone)
    {
        return Session.Rules.SoftRecall && !IsZoneSession && !zone.IsRegion && !zone.IsInstance &&
               !_questZones.Contains(zone.uid) &&
               !_pendingGuests.Values.Any(g => g.ZoneUid == zone.uid);
    }

    /// <summary>
    ///     That release is the answer to a soft recall and everything still stands: the host is on its way to that
    ///     very map, the player stood on it, and holds nothing else. Its visitors come back by their own release
    ///     (CanRejoinSoftlyAsGuest)
    /// </summary>
    private bool CanRejoinSoftly(ZoneLeaseRelease release, ISteamNetPeer peer)
    {
        return release is { Soft: true, Rejoin: true, Checkpoint: false, Map: not null, Arrival: null } &&
               release.StoodZoneUid == release.ZoneUid &&
               _pendingHostMove is { } move && move.Zone.uid == release.ZoneUid && move.Zone != _zone &&
               CanRecallSoftly(move.Zone) &&
               !(_leases.TryGetValue(peer.Id, out var held) && held.Count > 0);
    }

    /// <summary>
    ///     GitHub issue 46: the release of a visitor of the map the host walks onto (called back with it, see
    ///     HandOverZone), that stays on it as its holder does: the host is on its way there or already stands
    ///     there, nobody else holds it, and the visitor holds nothing
    /// </summary>
    private bool CanRejoinSoftlyAsGuest(ZoneLeaseRelease release, ISteamNetPeer peer)
    {
        if (release is not { Soft: true, Rejoin: true, Checkpoint: false, ZoneUid: -1, Arrival: null } ||
            release.StoodZoneUid <= 0 || game.spatials.Find(release.StoodZoneUid) is not { } zone ||
            (_leases.TryGetValue(peer.Id, out var held) && held.Count > 0) ||
            _leases.Values.Any(zones => zones.ContainsKey(zone.uid))) {
            return false;
        }

        var coming = _pendingHostMove is { } move && move.Zone == zone && zone != _zone;
        var there = _pendingHostMove is null && core.IsGameStarted && _zone == zone && zone.map is not null &&
                    pc.currentZone == zone;
        return (coming || there) && CanRecallSoftly(zone);
    }

    /// <summary>
    ///     Slice 2: that release walks into the map we stand on (the player took the stairs we took before it).
    ///     It came alone from a map it held alone, holds nothing else, and we are not on our way elsewhere: it
    ///     gets <see cref="ZoneSoftRejoin" /> (Travel) and this one map instead of the world, see SendSoftReturn
    /// </summary>
    private bool CanReturnSoftly(ZoneLeaseRelease release, ISteamNetPeer peer)
    {
        return release is { Soft: true, Rejoin: true, Checkpoint: false, Map: not null, Arrival: { } arrival } &&
               release.StoodZoneUid == release.ZoneUid && release.GuestCharas is not { Count: > 0 } &&
               _pendingHostMove is null && core.IsGameStarted && _zone is { map: not null } here &&
               arrival.ZoneUid == here.uid && release.ZoneUid != here.uid && pc.currentZone == here &&
               CanRecallSoftly(here) &&
               !(_leases.TryGetValue(peer.Id, out var held) && held.Count > 0);
    }

    /// <summary>
    ///     What a player that comes back without a copy of the world cannot know: taken right after what was
    ///     waiting to be told went out, see CompleteSoftRejoin
    /// </summary>
    private ZoneSoftRejoin CreateSoftRejoin(Chara chara, Zone zone, Dictionary<int, int> rebinds, bool travel)
    {
        var own = CompanionHelper.CompanionsOf(chara);
        return new() {
            ZoneUid = zone.uid,
            UidNext = game.cards.uidNext,
            Rebinds = rebinds,
            BagMix = NetDesync.Bag(chara),
            // walking in, it holds no copy of this map: the map itself follows
            MapSums = travel ? null : ZoneLeaseState.Sums(zone.map),
            Boxes = Enumerable.Range(0, 3)
                .Select(box => LZ4Bytes.Create(ShippingHelper.WorldBox(box)?.things.ToList() ?? []))
                .ToList(),
            GameDate = [..world.date.raw],
            Charas = zone.map.charas
                .Where(c => c.IsGlobal && c != chara && !own.Contains(c) && c.CompanionOwnerUid != chara.uid && !ServerAvatar.Is(c))
                .Select(c => LZ4Bytes.Create(c))
                .ToList(),
            Travel = travel,
        };
    }

    /// <summary>
    ///     Slice 2, in place of SendSaveProbe: the player is registered on our map without the world, told what
    ///     it cannot know (<see cref="ZoneSoftRejoin" />), then sent this one map as any player is sent a map.
    ///     From there the usual path: it answers ZoneDataReceivedResponse, is placed where it walks in (the
    ///     return spot noted by OnZoneLeaseRelease) and settled. All in this frame, as the world and its map are:
    ///     nothing happens between the numbers and the copy of the map
    /// </summary>
    private void SendSoftReturn(Chara chara, ISteamNetPeer peer, Dictionary<int, int> rebound)
    {
        var zone = _zone;

        EmpLog.Information("Soft return to {ZoneFullName}: player {@Peer} walks in, no world copy, {Rebound} card(s) renumbered",
            zone.ZoneFullName, peer, rebound.Count);

        RegisterPlayer(chara, peer);
        // kept for as long as the player may say it did not work: it then gets the world (OnZoneSoftRejoinFailed)
        _softRejoins[peer.Id] = (zone.uid, rebound, Time.unscaledTime + SoftFailSeconds, true);

        try {
            // as in SendSaveProbe: what is already done on this map is in the copy and in the numbers below, it
            // goes out before them. That player is still away and takes none of it; from this message on it
            // keeps what comes until the map is loaded (ElinDeltaManager.HoldForIncomingMap)
            Delta.RefreshBuffer();
            WorldStateDeltaUpdate();

            var rejoin = CreateSoftRejoin(chara, zone, rebound, true);

            EmpLog.Information("Soft return to {ZoneFullName}: telling player {@Peer} (uid counter {UidNext}, {Charas} character(s)), then the map",
                zone.ZoneFullName, peer, rejoin.UidNext, rejoin.Charas?.Count);

            if (!peer.Send(rejoin)) {
                FallBackToWorldCopy(peer, "not sent");
                return;
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Soft return to {ZoneFullName} could not be told to player {@Peer}", zone.ZoneFullName, peer);
            FallBackToWorldCopy(peer, "not told");
            return;
        }

        // who plays here, with the numbers of this map (not taken while away)
        peer.Send(SessionPlayersSnapshot.Create());
        PropagateZoneChangeState(zone, peer);
    }

    /// <summary>
    ///     As SendSaveProbe registers a player, without the world: its character follows the host into the map,
    ///     and is put back on its tile once the host stands there (the return spot noted by OnZoneLeaseRelease)
    /// </summary>
    private void RegisterSoftRejoin(Chara chara, ISteamNetPeer peer, int zoneUid, Dictionary<int, int> rebound)
    {
        EmpLog.Information("Soft rejoin of {ZoneUid}: player {@Peer} stays on its map, no world copy, {Rebound} card(s) renumbered",
            zoneUid, peer, rebound.Count);

        RegisterPlayer(chara, peer);
        _softRejoins[peer.Id] = (zoneUid, rebound, Time.unscaledTime + SoftEnterSeconds, false);

        // a visitor whose release came once we stood there (its holder's release let our move go on): told now
        if (_pendingHostMove is null && core.IsGameStarted && _zone is { map: not null } here && here.uid == zoneUid &&
            pc.currentZone == here) {
            CompleteSoftRejoin(peer, here);
        }
    }

    /// <summary>
    ///     Zone session, GitHub issue 46: visitor -> the map it stays on (it stood there when the map came to us, its
    ///     tile is fresh), until it joins: it gets <see cref="ZoneSoftRejoin" /> instead of our world, see
    ///     TrySendSoftStay
    /// </summary>
    private readonly Dictionary<ulong, int> _softStays = [];

    /// <summary>
    ///     Zone session: the visitor announced by the host stands on our map already (the host left it, or the
    ///     player it visited did, see ElinNetClient.StayAsGuest and HandOverZone)
    /// </summary>
    private void NoteSoftStay(ZoneGuestRequest request)
    {
        if (Session.Rules.SoftRecall && request is { Stood: not null, StoodStale: false, Arrival: null } &&
            _zone?.uid == request.ZoneUid) {
            _softStays[request.GuestUser] = request.ZoneUid;
        } else {
            _softStays.Remove(request.GuestUser);
        }
    }

    /// <summary>
    ///     Zone session, in place of SendSaveProbe: a visitor that stood on our map before we ran it keeps its game
    ///     and its scene. It is registered as one that joins, told what it cannot know (<see cref="ZoneSoftRejoin" />
    ///     with the numbers of our map), and answers as a player whose map is loaded (ZoneDataReceivedResponse), or
    ///     says it did not work and gets our world (OnZoneSoftRejoinFailed)
    /// </summary>
    /// <returns>false when the world goes as before</returns>
    private bool TrySendSoftStay(Chara chara, ISteamNetPeer peer)
    {
        if (!IsZoneSession || !_softStays.Remove(peer.User, out var zoneUid) || !core.IsGameStarted ||
            _zone is not { map: not null } zone || zone.uid != zoneUid) {
            return false;
        }

        RegisterPlayer(chara, peer);
        // as long as it may say it did not work: it then gets the world
        _softRejoins[peer.Id] = (zone.uid, [], Time.unscaledTime + SoftFailSeconds, true);

        try {
            // what is already done here is in the numbers below: it goes out before them (the visitor, still at
            // its handshake, takes none of it)
            Delta.RefreshBuffer();
            WorldStateDeltaUpdate();

            var stay = CreateSoftRejoin(chara, zone, [], false);
            // the boxes of the world are the host's: emptied in the game of every player away, ours and the visitor's
            stay.Boxes = null;

            EmpLog.Information("Visitor {@Peer} stays on {ZoneFullName}: no world copy (uid counter {UidNext}, {Charas} character(s), map {Sums})",
                peer, zone.ZoneFullName, stay.UidNext, stay.Charas?.Count, ZoneLeaseState.TellSums(stay.MapSums ?? []));

            peer.Send(NetSession.Instance.Rules);
            if (!peer.Send(stay)) {
                FallBackToWorldCopy(peer, "not sent");
                return true;
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Staying on {ZoneFullName} could not be told to visitor {@Peer}", zone.ZoneFullName, peer);
            FallBackToWorldCopy(peer, "not told");
            return true;
        }

        peer.Send(SessionPlayersSnapshot.Create());
        return true;
    }

    /// <summary>
    ///     The host tells everyone the map it just entered: the players who stay on that very map are not sent it.
    ///     Those who waited for another map (the host went elsewhere meanwhile) get the world
    /// </summary>
    private List<ISteamNetPeer> TakeSoftRejoins(Zone zone)
    {
        var staying = new List<ISteamNetPeer>();
        if (_softRejoins.Count == 0) {
            return staying;
        }

        foreach (var (peerId, soft) in _softRejoins.ToList()) {
            if (soft.Entered) {
                continue;
            }

            if (Socket.Peers.FirstOrDefault(p => p.Id == peerId) is not { } peer) {
                _softRejoins.Remove(peerId);
                continue;
            }

            if (soft.ZoneUid != zone.uid || zone != _zone || zone.map is null) {
                FallBackToWorldCopy(peer, "the host is not on that map");
                continue;
            }

            staying.Add(peer);
        }

        return staying;
    }

    /// <summary>
    ///     The host stands on the map that player handed back and never left: it is told so, with what it cannot
    ///     know without a copy of the world, then placed as a player whose map was loaded
    /// </summary>
    private void CompleteSoftRejoin(ISteamNetPeer peer, Zone zone)
    {
        if (!_softRejoins.TryGetValue(peer.Id, out var soft) || !ActiveRemoteCharas.TryGetValue(peer.Id, out var chara)) {
            // that player was left out of the map broadcast and waits for an answer: none can be made here, its
            // link is closed and it comes back as a player who arrives (world, then map)
            if (_softRejoins.Remove(peer.Id)) {
                EmpLog.Warning("Soft rejoin of player {@Peer} has no character left, closing its link", peer);
                Socket.Disconnect(peer, EmpDisconnectInfo.RemoteClosed);
            }

            return;
        }

        try {
            // as in SendSaveProbe: what is already done on this map is in the numbers below, it goes out before
            // them. That player is still away and takes none of it (ElinNetClient.ApplyChatWhileAway); what
            // comes after this message happened after, and is applied to a map that has the same numbers
            Delta.RefreshBuffer();
            WorldStateDeltaUpdate();

            var rejoin = CreateSoftRejoin(chara, zone, soft.Rebinds, false);

            EmpLog.Information("Soft rejoin of {ZoneFullName}: host stands there, telling player {@Peer} (uid counter {UidNext}, {Charas} character(s), map {Sums})",
                zone.ZoneFullName, peer, rejoin.UidNext, rejoin.Charas?.Count, ZoneLeaseState.TellSums(rejoin.MapSums ?? []));

            if (!peer.Send(rejoin)) {
                FallBackToWorldCopy(peer, "not sent");
                return;
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Soft rejoin of {ZoneFullName} could not be told to player {@Peer}", zone.ZoneFullName, peer);
            FallBackToWorldCopy(peer, "not told");
            return;
        }

        _softRejoins[peer.Id] = (soft.ZoneUid, soft.Rebinds, Time.unscaledTime + SoftFailSeconds, true);

        // who plays here, with the numbers of this map (not taken while away)
        peer.Send(SessionPlayersSnapshot.Create());

        // from here as a player whose copy of the map is loaded: its tile, its companions, what it is owed
        OnZoneDataReceivedResponse(new ZoneDataReceivedResponse {
            ZoneUid = zone.uid,
        }, peer);
    }

    /// <summary>
    ///     Net event: the return in place did not work in that player's game, it gets the world
    /// </summary>
    private void OnZoneSoftRejoinFailed(ZoneSoftRejoinFailed failed, ISteamNetPeer peer)
    {
        if (!_softRejoins.TryGetValue(peer.Id, out var soft) || soft.ZoneUid != failed.ZoneUid) {
            // the world is already on its way (our own fallback), or was sent in the first place
            EmpLog.Debug("Player {@Peer} gave up a soft rejoin of {ZoneUid} we do not wait for", peer, failed.ZoneUid);
            return;
        }

        // text written by another game: cut before it reaches this journal
        var reason = failed.Reason is { Length: > 200 } text ? text.Substring(0, 200) : failed.Reason;
        FallBackToWorldCopy(peer, $"its game says: {reason}");
    }

    /// <summary>
    ///     Never a question to the player, never a player left waiting: the world and the map, as without the rule
    /// </summary>
    private void FallBackToWorldCopy(ISteamNetPeer peer, string reason)
    {
        var entered = _softRejoins.Remove(peer.Id, out var soft) && soft.Entered;
        if (!ActiveRemoteCharas.TryGetValue(peer.Id, out var chara)) {
            return;
        }

        EmpLog.Warning("Soft rejoin of {ZoneUid} given up for player {@Peer} ({Reason}), sending the world",
            soft.ZoneUid, peer, reason);

        // registered again by SendSaveProbe
        _settled.Remove(peer.Id);
        if (States.Remove(peer.Id, out var state)) {
            Session.CurrentPlayers.Remove(state);
        }

        // it stands here already: the map loaded under it must not push it next to us, see OnZoneDataReceivedResponse
        // (a spot still noted is the one to keep: the way it walks in by, not used yet, see SendSoftReturn)
        if (entered && !_returnSpots.ContainsKey(peer.User) && _zone is { } here && here.uid == soft.ZoneUid &&
            chara.pos is { IsValid: true } stood) {
            _returnSpots[peer.User] = (here.uid, stood.Copy(), null, Time.unscaledTime + ReturnSpotSeconds, false);
        }

        SendSaveProbe(chara, peer);
    }

    private void UpdateSoftRejoins()
    {
        if (_softRejoins.Count == 0) {
            return;
        }

        var now = Time.unscaledTime;
        foreach (var (peerId, soft) in _softRejoins.ToList()) {
            if (now < soft.Deadline) {
                continue;
            }

            if (soft.Entered || Socket.Peers.FirstOrDefault(p => p.Id == peerId) is not { } peer) {
                _softRejoins.Remove(peerId);
                continue;
            }

            // the move did not happen (refused, a map that does not load): the player must not wait for good
            FallBackToWorldCopy(peer, "the host did not enter the map");
        }
    }
}
