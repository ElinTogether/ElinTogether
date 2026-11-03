using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.LangMod;
using ElinTogether.Models;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Independent travel: go to a zone the host is not in, simulate it locally, hand it back when leaving <br />
///     A zone another player simulates is joined as a guest of its zone session, see NetSession.ZoneSession
/// </summary>
internal partial class ElinNetClient
{
    private (Zone Zone, ZoneTransition Transition)? _pendingTravel;

    /// <summary>
    ///     Lease granted while on the host map (or as a guest), waiting for <see cref="ZoneLeaseDepart" />
    /// </summary>
    private ZoneLeaseGrant? _pendingGrant;

    /// <summary>
    ///     Zones this client created itself (world map fields, dungeon floors...), unknown to the host until leased
    /// </summary>
    private readonly HashSet<Zone> _localZones = [];

    private float _nextCheckpoint;

    /// <summary>
    ///     Zone of the host while away, kept up to date from its zone changes
    /// </summary>
    private int _hostZoneUid = -1;

    internal void OnLocalZoneCreated(Zone zone)
    {
        _localZones.Add(zone);
    }

    /// <summary>
    ///     Away zone handed back, waiting for the host save probe
    /// </summary>
    private bool _rejoining;

    /// <summary>
    ///     Guest whose zone owner left: how long we wait for the host to hand the zone over before going back
    /// </summary>
    private const float HandoffWaitSeconds = 30f;

    private float _handoffDeadline;

    /// <summary>
    ///     Zone the host moved to while we were leaving its map to stay behind, see StayAsGuest
    /// </summary>
    private int? _hostZoneAfterDeparture;

    /// <summary>
    ///     Where the player picked to come back after its death, travelled to once the host revived it
    /// </summary>
    private (Zone Zone, ZoneTransition Transition)? _deathTravel;

    /// <summary>
    ///     The host revived the local player (CharaReviveDelta): it goes where it picked on the death screen, its
    ///     home or the last town, by the same independent travel as any other walk out of the host's map
    /// </summary>
    internal void OnRevived()
    {
        if (_deathTravel is not { } travel) {
            return;
        }

        _deathTravel = null;
        if (Session.IsAway || !Session.Rules.AllowIndependentTravel || pc.isDead || travel.Zone == pc.currentZone) {
            return;
        }

        EmpLog.Information("Revived, going back to {ZoneFullName}", travel.Zone.ZoneFullName);
        pc.MoveZone(travel.Zone, travel.Transition);
    }

    /// <summary>
    ///     The local player moves to a zone other than the one it replicates from the host <br />
    ///     Returns true to let the move happen now
    /// </summary>
    internal bool TryTravel(Zone? zone, ZoneTransition transition)
    {
        // moving into the zone just granted (not a null zone: away from nothing, that would let a guest's
        // MoveZone(null) through, see the death move below)
        if (zone is not null && Session.AwayZone == zone && !_rejoining) {
            return true;
        }

        if (!Session.IsAway && (pc.isDead || player.deathZoneMove)) {
            // the host revives remote players where they fell, see CharaReviveEvent. Only that move is dropped:
            // the game clears the mark inside the move we skip, left set it refused every travel that followed.
            // With independent travel the player then goes where it picked, as the host does (OnRevived); without
            // it, it cannot leave the host's map alone and stays where it was revived
            _deathTravel = null;
            if (player.deathZoneMove && Session.Rules.AllowIndependentTravel) {
                // neither a respawn point nor a home of its own (OwnSettings.Carry): the start site, or the last town
                zone ??= OwnSettings.RespawnFallback();
                _deathTravel = zone is null ? null : (zone, transition);
            }

            player.deathZoneMove = false;
            return false;
        }

        // away alone the game revived it (Scene, the death choice) and sends it to its respawn point, then its home:
        // with neither of its own (OwnSettings.Carry), where the host's map sends it (above)
        if (zone is null && player.deathZoneMove && !pc.isDead) {
            zone = OwnSettings.RespawnFallback();
            if (zone == pc.currentZone && zone is not null) {
                player.deathZoneMove = false;
                pc.Teleport(_zone.GetSpawnPos(pc, ZoneTransition.EnterState.Return).GetNearestPoint() ?? _map.GetCenterPos(), true, true);
                return false;
            }
        }

        if (zone is null) {
            return false;
        }

        // GitHub issue 30 (a scroll of return took a guest to the world map): the game picks the world map when the
        // destination the player chose is unknown or was never chosen (the list was not answered), see Chara.Tick.
        // Say what it had, so the next report names the cause
        if (transition.state == ZoneTransition.EnterState.Return && player.returnInfo is { } ret) {
            EmpLog.Information(
                "Return to {ZoneFullName}: destination uid {Dest} (known {Known}), still asking {Asking}, from {Here}",
                zone.ZoneFullName, ret.uidDest, ret.uidDest != 0 && game.spatials.map.ContainsKey(ret.uidDest), ret.askDest,
                pc.currentZone?.ZoneFullName);
        }

        // any other travel forgets a death choice the host never answered (a lost link, a crash)
        _deathTravel = null;

        // leaving an instance, Elin sends the player back where it started, see Chara.MoveZone
        if (pc.currentZone?.instance is { } instance) {
            zone = game.spatials.Find(instance.uidZone) ?? pc.homeZone;

            // out of a bout with an adventurer: the host puts its adventurer back (AdventurerBout)
            if (instance is ZoneInstanceBout bout) {
                AdventurerBout.Leave(bout);
            }

            // the zone of our quest, run by the host who came along: it takes everyone out, and tells us how
            // the quest went on the way, see ElinNetHost.LeaveAccompaniedZone
            if (!Session.IsAway && PersonalQuests.InstancesEnabled &&
                instance is ZoneInstanceRandomQuest { uidQuest: not 0 } ours &&
                game.quests.list.Exists(q => q.uid == ours.uidQuest && PersonalQuests.IsPersonal(q))) {
                Delta.AddRemote(new QuestFollowDelta {
                    Kind = QuestFollowDelta.Leave,
                    Name = pc.Name,
                    ZoneUid = pc.currentZone.uid,
                });
                return false;
            }

            // how the quest went is settled now: where we arrive may be someone else's map
            PersonalQuests.LeaveInstance(pc.currentZone);
        }

        if (!Session.Rules.AllowIndependentTravel) {
            EmpPop.Debug("emp_party_gather".lang());
            return false;
        }

        if (_pendingTravel is not null || _rejoining) {
            // a request is in flight
            return false;
        }

        // a guest leaves the zone session first, its owner applies our last actions, see OnGuestLeftZone
        if (Session.IsGuest && Session.ZoneSession is ElinNetClient zoneClient) {
            _pendingTravel = (zone, transition);
            StopWalk();
            zoneClient.LeaveZoneSession(Session.AwayZone!.uid);
            EmpPop.Debug("emp_travel_requesting".lang());
            return false;
        }

        if (Session.IsAway && IsHostZoneToRejoin(zone)) {
            EmpLog.Information("Returning from {AwayZone} to host zone {ZoneFullName}",
                Session.AwayZone!.ZoneFullName, zone.ZoneFullName);

            StopWalk();
            SendRejoin(ZoneArrival.Create(zone.uid, transition));
            EmpPop.Debug("emp_travel_returning".lang());
            return false;
        }

        StopWalk();
        RequestLease(zone, transition);
        return false;
    }

    /// <summary>
    ///     Going where the host stands is coming back to it, except to the world map: everyone walks its own copy of
    ///     it (OnHostZoneChangedWhileAway, ElinNetHost.GetLeaseDenyReason). Taken for a return, a player leaving its
    ///     home for the world map while the host was on it came back to the host, and landed wherever the host had
    ///     walked into meanwhile, a dungeon beside it instead of the world map (GitHub issue 50)
    /// </summary>
    private bool IsHostZoneToRejoin(Zone zone)
    {
        return zone.uid == _hostZoneUid && !_localZones.Contains(zone) && !zone.IsRegion;
    }

    /// <summary>
    ///     The game stops the walk at the step onto the stairs (Chara.MoveZone, skipped while the host answers): kept
    ///     walking, the player landed in front of the stairs it came by and stepped back in (GitHub issue 10, Vernis
    ///     mine)
    /// </summary>
    private static void StopWalk()
    {
        pc.ai?.Cancel();
        player.nextMove = UnityEngine.Vector2.zero;
        EInput.Consume(consumeAxis: true);
    }

    /// <summary>
    ///     When this game last arrived on a map it was handed: the stairs there do not take it in by a step for a
    ///     moment, see <see cref="Patches.ArrivalStairsPatch" />
    /// </summary>
    internal static float ArrivedAt { get; private set; } = float.MinValue;

    private void RequestLease(Zone zone, ZoneTransition transition)
    {
        EmpLog.Information("Requesting zone lease {ZoneFullName}",
            zone.ZoneFullName);

        _pendingTravel = (zone, transition);
        Host.Send(ZoneLeaseRequest.Create(zone, _localZones.Contains(zone)));
        EmpPop.Debug("emp_travel_requesting".lang());
    }

    /// <summary>
    ///     Net event: Travel accepted, this client simulates the zone from now on (or joins its owner)
    /// </summary>
    private void OnZoneLeaseGrant(ZoneLeaseGrant grant)
    {
        if (grant.Handoff) {
            TakeOverZone(grant);
            return;
        }

        if (_pendingTravel is not { } travel || travel.Zone.uid != grant.RequestedUid) {
            EmpLog.Warning("Received unexpected lease for zone {ZoneUid}", grant.RequestedUid);
            Host.Send(new ZoneLeaseDecline {
                ZoneUid = grant.ZoneUid,
            });
            return;
        }

        AdoptHostUid(travel.Zone, grant.ZoneUid);

        // a thing held on the mouse is in no bag: back in it before our last actions and our character go out
        // (GitHub issue 48)
        HeldDrag.PutBack("leaving for " + travel.Zone.ZoneFullName);

        if (grant.Guest) {
            // leave where we are (handing our zone back if we hold one, or only our character when we were
            // a guest: the host forwards it to the owner of the zone we go to), then that owner expects us,
            // see ZoneLeaseDepart
            if (Session.IsAway) {
                HandBackAwayZone(false);
            } else if (!Session.IsAway) {
                FlushDeltasNow();
            }

            Host.Send(new ZoneLeaseAck {
                ZoneUid = grant.ZoneUid,
                Arrival = ZoneArrival.Create(grant.ZoneUid, travel.Transition),
            });
            _pendingGrant = grant;
            StopWorldStateUpdate();
            return;
        }

        if (Session.IsAway) {
            _pendingTravel = null;

            // hand back the zone we are leaving, while it is still active; a guest holds none and only
            // brings its character up to date on the host
            HandBackAwayZone(false);

            TravelTo(travel.Zone, travel.Transition, grant);
            return;
        }

        // leaving the host map: everything done there reaches the host first, then we wait for
        // the results of those actions before going, see ZoneLeaseDepart
        FlushDeltasNow();
        Host.Send(new ZoneLeaseAck {
            ZoneUid = grant.ZoneUid,
        });
        _pendingGrant = grant;
        StopWorldStateUpdate();
    }

    /// <summary>
    ///     Acknowledged a departure: the host already took us off its map and its player list,
    ///     only the results of our last actions still matter until ZoneLeaseDepart
    /// </summary>
    private bool IsAwaitingDeparture => _pendingGrant is not null && !Session.IsAway;

    /// <summary>
    ///     Net event: The host applied our last actions on its map and sent their results, now we go
    /// </summary>
    private void OnZoneLeaseDepart(ZoneLeaseDepart depart)
    {
        // the zone we visit changed hands, its new owner expects us, see HandOverZone
        if (_pendingTravel is null && _pendingGrant is null && depart.Guest is { } moved &&
            Session.IsGuest && Session.AwayZone is { } here && here.uid == depart.ZoneUid) {
            _handoffDeadline = 0;
            JoinZoneSession(here, moved);
            return;
        }

        if (_pendingTravel is not { } travel || _pendingGrant is not { } grant || grant.ZoneUid != depart.ZoneUid) {
            EmpLog.Warning("Received unexpected departure for zone {ZoneUid}", depart.ZoneUid);
            return;
        }

        _pendingTravel = null;
        _pendingGrant = null;

        // the results arrived right before this packet, apply them while still synced
        WorldStateDeltaProcess();

        if (depart.Guest is { } address) {
            JoinZoneSession(travel.Zone, address);
            _visitTravel = (travel.Zone.uid, travel.Transition);
            return;
        }

        TravelTo(travel.Zone, travel.Transition, grant);
    }

    /// <summary>
    ///     A zone created here gets the uid the host assigned, nothing refers to it by uid yet
    /// </summary>
    private void AdoptHostUid(Zone zone, int uid)
    {
        _localZones.Remove(zone);

        if (zone.uid == uid) {
            return;
        }

        var spatials = game.spatials;
        spatials.uidNext = Math.Max(spatials.uidNext, uid + 1);

        if (spatials.map.TryGetValue(uid, out var other) && other != zone) {
            spatials.map.Remove(uid);

            if (other.id == zone.id && other.x == zone.x && other.y == zone.y) {
                // host copy announced through SpatialGenDelta before the grant
                other.parent?.RemoveChild(other);
            } else {
                // another zone created here took that uid
                spatials.AssignUID(other);
            }
        }

        EmpLog.Debug("Zone {ZoneFullName} created locally as {LocalUid}, host uid {ZoneUid}",
            zone.ZoneFullName, zone.uid, uid);

        // what led there (stairs, a tent) points at the zone by number
        var local = zone.uid;
        foreach (var thing in _map.things.Concat(pc.things.Flatten().OfType<Thing>())) {
            if (thing.c_uidZone == local) {
                thing.c_uidZone = uid;
            }
        }

        spatials.map.Remove(zone.uid);
        zone.uid = uid;
        spatials.map[uid] = zone;

        // the zone of a quest sits on the tile of the town it comes from, without taking its place on the map
        if (zone.parent is Region region && !zone.IsInstance) {
            region.elomap.SetZone(zone.x, zone.y, zone, true);
        }
    }

    private void TravelTo(Zone zone, ZoneTransition transition, ZoneLeaseGrant grant)
    {
        if (grant.Map is not null) {
            if (zone.map is not null) {
                // stale copy from an earlier visit alongside the host
                zone.UnloadMap();
            }

            ZoneLeaseState.WriteMap(zone, grant.Map);
        }

        ZoneLeaseState.ApplyState(zone, grant.ZoneState, grant.IdCurrentSubset);
        Helper.LeasedBase.Apply(zone, grant.Branch, grant.BaseElements, grant.Name);
        // cards created here get uids the host does not use
        game.cards.uidNext = Math.Max(game.cards.uidNext, grant.UidRangeStart);
        AdoptQuestUidRange(grant);

        EnterAway(zone);
        Session.IsGuest = false;

        EmpLog.Information("Leased zone {ZoneFullName}, uid range from {UidRangeStart}, map {HasMap}",
            zone.ZoneFullName, grant.UidRangeStart, grant.Map is not null);

        ArrivedAt = Time.realtimeSinceStartup;
        CloseDramas();
        KeepOtherPlayersOff(zone);
        pc.MoveZone(zone, transition);
    }

    /// <summary>
    ///     GitHub issue 57: our world still puts the other players (and their companions) where we last saw them.
    ///     Coming back alone to that map, the game placed them on it (Zone.AddGlobalCharasOnActivate) and, as nobody
    ///     plays them in this game while away, ran them as characters of the map: the host stood, played music and
    ///     opened doors on the guest's screen while it was in another zone. Nobody else is on a map we simulate
    ///     alone: they come back with the world, or the host lists them (AdoptHostCharas), when we return
    /// </summary>
    private void KeepOtherPlayersOff(Zone zone)
    {
        var players = Session.CurrentPlayers.Where(p => p is not null).Select(p => p.CharaUid).ToHashSet();
        foreach (var chara in game.cards.globalCharas.Values.ToList()) {
            if (chara == pc || chara.currentZone != zone || chara.IsCompanionOf(pc) || chara.CompanionOwnerUid == pc.uid) {
                continue;
            }

            // a companion of the party leader (the host) has no owner written
            var hostsCompanion = chara.CompanionOwnerUid == 0 && chara.party is { } party && party == pc.party &&
                                 party.members.Contains(chara) && party.leader != pc;
            if (!players.Contains(chara.uid) && !chara.GetBool("remote_chara") && chara.CompanionOwnerUid == 0 && !hostsCompanion) {
                continue;
            }

            if (chara.parent is Zone) {
                chara.parent = null;
            }

            chara.currentZone = null;
            EmpLog.Debug("Character {Uid} of another player kept off {ZoneFullName}, which we hold alone",
                chara.uid, zone.ZoneFullName);
        }
    }

    /// <summary>
    ///     The game holds the map change while a drama layer is open (Scene.Update: !LayerDrama.IsActive()). The
    ///     bout's "Yes" moves the player from the drama's own kill callback, so a layer that is still listed then
    ///     (the adventurer's menu, seen with LayerDrama.Instance already null) kept the granted zone from loading for
    ///     90 s. Closed with the game's own kill, which also runs the callbacks the dialog left
    /// </summary>
    private static void CloseDramas()
    {
        foreach (var layer in ui.layers.OfType<LayerDrama>().ToArray()) {
            layer.Close();
        }
    }

    /// <summary>
    ///     Quests created here (residents of the zone we simulate) get uids the host does not use
    /// </summary>
    private static void AdoptQuestUidRange(ZoneLeaseGrant grant)
    {
        if (grant.QuestUidRangeStart > 0) {
            game.quests.uid = Math.Max(game.quests.uid, grant.QuestUidRangeStart);
        }
    }

    /// <summary>
    ///     Another player simulates the zone: connect to its zone session, it sends its world and the zone
    /// </summary>
    private void JoinZoneSession(Zone zone, ZoneGuestAddress address)
    {
        EnterAway(zone);
        Session.IsGuest = true;

        // we stay on that very map (GitHub issue 46): its keeper may switch us in place, nothing moves until it answers
        BeginSoftStay(zone);

        EmpLog.Information("Joining the zone session of {RemoteIdentity} in {ZoneFullName}",
            address.HostUser, zone.ZoneFullName);

        // the holder says how it is reached, not our own link to the host: a game joined to the host over loopback
        // (the owner of a hidden server) still dials a friend's zone through Steam
        var session = Session.InitializeZoneSession<ElinNetClient>();
        if (address.Local) {
            session.ConnectLocalPort((ushort)address.Port);
        } else {
            session.ConnectSteamUser(address.HostUser, address.VirtualPort);
        }
    }

    /// <summary>
    ///     Zone session client: leaving the zone, send our last actions and wait for their results
    /// </summary>
    internal void LeaveZoneSession(int zoneUid)
    {
        // (issue 48) what is held on the mouse goes with our last actions
        HeldDrag.PutBack("leaving a zone session");
        FlushDeltasNow();
        Host.Send(new ZoneGuestLeave {
            ZoneUid = zoneUid,
        });
    }

    /// <summary>
    ///     Zone session client, net event: the owner applied our last actions, we may go
    /// </summary>
    private void OnZoneGuestLeft(ZoneGuestLeft left)
    {
        if (!IsZoneSession) {
            return;
        }

        WorldStateDeltaProcess();
        (Session.Transport as ElinNetClient)?.OnGuestLeftZone();
    }

    /// <summary>
    ///     Out of the zone session, go on with the travel that started it
    /// </summary>
    internal void OnGuestLeftZone()
    {
        _visitTravel = null;
        Session.RemoveZoneSession();

        if (_pendingTravel is not { } travel) {
            return;
        }

        if (IsHostZoneToRejoin(travel.Zone)) {
            _pendingTravel = null;
            SendRejoin(ZoneArrival.Create(travel.Zone.uid, travel.Transition));
            return;
        }

        RequestLease(travel.Zone, travel.Transition);
    }

    /// <summary>
    ///     The zone session ended on its own: the zone owner left or dropped, back to the host
    /// </summary>
    internal void OnZoneSessionEnded(ElinNetBase session, string reason)
    {
        if (session is not ElinNetClient || !Session.IsGuest) {
            return;
        }

        EndSoftStay();

        // meanwhile we play on alone: the host gives the zone to one of us (or calls us back),
        // see TakeOverZone, and only if it stays silent we go back
        var visit = _visitTravel;
        _visitTravel = null;
        _pendingTravel = null;

        // GitHub issue 42: the owner left while we were still on our way in (its world loaded here, not the map
        // yet). The zone it hands over cannot be taken without a map to stand on (TakeOverZone), and the 30 s wait
        // ended on the host's map, far from where the player went. Back to the host now, then on to that zone by
        // the same travel, which the host grants as any free zone
        if (visit is { } going && Session.AwayZone?.uid == going.ZoneUid && _zone?.uid != going.ZoneUid) {
            EmpLog.Warning("Zone session ended ({Reason}) before its map came, back to the host, then on to zone {ZoneUid}",
                reason, going.ZoneUid);
            _resumeTravel = (going.ZoneUid, going.Transition, Time.realtimeSinceStartup + ResumeTravelSeconds);
            _handoffDeadline = Time.realtimeSinceStartup;
            return;
        }

        // meanwhile we play on alone: the host gives the zone to one of us (or calls us back),
        // see TakeOverZone, and only if it stays silent we go back
        EmpLog.Warning("Zone session ended ({Reason}), waiting for the host to hand the zone over",
            reason);

        _handoffDeadline = Time.realtimeSinceStartup + HandoffWaitSeconds;
    }

    /// <summary>
    ///     The zone of the zone session we were joining, by the travel that led there (issue 42)
    /// </summary>
    private (int ZoneUid, ZoneTransition Transition)? _visitTravel;

    /// <summary>
    ///     That zone, travelled to again once back on the host's map, until the deadline
    /// </summary>
    private (int ZoneUid, ZoneTransition Transition, float Until)? _resumeTravel;

    private const float ResumeTravelSeconds = 180f;

    private float _resumeAt;

    private void UpdateResumeTravel()
    {
        if (_resumeTravel is not { } going) {
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now > going.Until) {
            _resumeTravel = null;
            _resumeAt = 0;
            return;
        }

        // standing on the host's map again, the world copy loaded and placed
        if (Session.IsAway || _rejoining || _pendingTravel is not null || _pendingGrant is not null || !core.IsGameStarted ||
            _zone is null || _awaitingActivation != 0 || pc?.currentZone != _zone) {
            _resumeAt = 0;
            return;
        }

        if (_resumeAt <= 0) {
            _resumeAt = now + 2f;
            return;
        }

        if (now < _resumeAt) {
            return;
        }

        _resumeTravel = null;
        _resumeAt = 0;
        if (game.spatials.Find(going.ZoneUid) is not { } zone || zone == _zone || pc.isDead || !Session.Rules.AllowIndependentTravel) {
            return;
        }

        EmpLog.Information("Going on to {ZoneFullName}, where the player went when its owner left", zone.ZoneFullName);
        pc.MoveZone(zone, going.Transition);
    }

    private void UpdateHandoffWait()
    {
        if (_handoffDeadline <= 0 || Time.realtimeSinceStartup < _handoffDeadline) {
            return;
        }

        _handoffDeadline = 0;

        if (!Session.IsGuest || Session.ZoneSession is not null || _rejoining) {
            return;
        }

        EmpLog.Warning("Nobody took the zone over, returning to the host");
        EmpPop.Information("emp_travel_zone_closed".lang());
        SendRejoin();
    }

    /// <summary>
    ///     Net event: the owner of the zone we visit left it, we simulate it from now on, as it is here
    /// </summary>
    private void TakeOverZone(ZoneLeaseGrant grant)
    {
        // either we visit the zone of a player who left it, or we stand on the host map and the host leaves it
        var visiting = Session.IsGuest && Session.AwayZone is { } away && away.uid == grant.ZoneUid && _zone == away;
        var withHost = !Session.IsAway && _zone?.uid == grant.ZoneUid;

        if (withHost && grant.Guest) {
            StayAsGuest(grant);
            return;
        }

        if ((!visiting && !withHost) || _rejoining) {
            // gone meanwhile, the host drops that lease when we rejoin
            EmpLog.Warning("Handed zone {ZoneUid} while not in it (here {Here}, away {Away}, rejoining {Rejoining})",
                grant.ZoneUid, _zone?.uid, Session.AwayZone?.uid, _rejoining);

            // not away and not on our way back: we still follow the host (that map was not on our screen yet),
            // but the host took our character off its map with this message and answers nothing we do from
            // here. Back to it the way a player away comes back: it drops that lease and puts us on its map
            if (!Session.IsAway && !_rejoining && core.IsGameStarted) {
                SendRejoin();
            } else if (Session.IsAway && !_rejoining && Session.AwayZone?.uid != grant.ZoneUid) {
                // away elsewhere: nothing drops that lease on the host, the map stayed held by nobody for good, nobody
                // else could go in (reliability audit 2026-10-27, #4)
                Host.Send(new ZoneLeaseDecline {
                    ZoneUid = grant.ZoneUid,
                });
            }

            return;
        }

        if (_zone is not { } zone) {
            return;
        }

        // everyone else leaves this copy of the map: the players (with the host), their companions,
        // those staying join us again with theirs. Listed while the party and the player list still stand
        var players = Session.CurrentPlayers.Where(p => p is not null).Select(p => p.CharaUid).ToHashSet();
        List<Chara> Leaving() => _map.charas
            .Where(c => c != pc && (players.Contains(c.uid) || c.GetBool("remote_chara") ||
                                    (c.party is not null && c.party == pc.party && !c.IsCompanionOf(pc)) ||
                                    (c.CompanionOwnerUid != 0 && c.CompanionOwnerUid != pc.uid)))
            .ToList();
        var leaving = Leaving();

        // cards created here get uids the host does not use (before a map is loaded here on our own)
        game.cards.uidNext = Math.Max(game.cards.uidNext, grant.UidRangeStart);
        AdoptQuestUidRange(grant);

        if (withHost) {
            // the results of our last actions on the host map arrived right before this packet
            WorldStateDeltaProcess();

            // while still a client of the host: its map is loaded as any map it sends
            if (AdoptHostCopy(zone, grant, "the host")) {
                leaving = Leaving();
            }

            if (_rejoining) {
                // the copy could not be loaded, we are on our way back to the host
                _pendingTravel = null;
                _handoffDeadline = 0;
                return;
            }

            // (issue 48) held on the mouse, in no bag: back in it while this game keeps our character
            HeldDrag.PutBack("keeping the map the host left");
            EnterAway(zone);
        } else {
            // still open if the owner has not closed it yet
            Session.RemoveZoneSession();
            Session.IsGuest = false;

            // the copy the owner handed back to the host is the one everyone else gets: loaded as a leased map is
            if (AdoptHostCopy(zone, grant, "its owner")) {
                leaving = Leaving();
            }

            if (_rejoining) {
                _pendingTravel = null;
                _handoffDeadline = 0;
                return;
            }
        }

        Session.IsGuest = false;
        // the base as the world keeps it: what this game simulates from now on goes back from there
        Helper.LeasedBase.Apply(zone, grant.Branch, grant.BaseElements, grant.Name);
        // our own move asked just before the host left (to another map than this one) goes on: its grant comes next,
        // and finds us holding this map as any player away who moves on. Dropped here, the grant was "unexpected"
        // and refused, and we stayed behind instead of going where we went (simultaneous moves, 2026-10-14)
        if (_pendingTravel is { } going && going.Zone.uid == grant.ZoneUid) {
            _pendingTravel = null;
        }

        _handoffDeadline = 0;
        _nextCheckpoint = Time.realtimeSinceStartup + Session.Rules.TravelCheckpointSeconds;

        // those still waiting for a uid from the owner get one of ours
        foreach (var card in _map.things.Concat<Card>(_map.charas).ToList()) {
            if (PendingUid.IsPending(card.uid)) {
                game.cards.AssignUID(card);
            }

            foreach (var thing in card.things.Flatten()) {
                if (PendingUid.IsPending(thing.uid)) {
                    game.cards.AssignUID(thing);
                }
            }
        }

        foreach (var chara in leaving) {
            if (chara.party is { } party && party.members.Contains(chara)) {
                party.RemoveMember(chara);
            }

            if (chara.parent is Zone) {
                zone.RemoveCard(chara);
            }
        }

        EmpLog.Information("Took over zone {ZoneFullName}, uid range from {UidRangeStart}, from the host {WithHost}",
            zone.ZoneFullName, grant.UidRangeStart, withHost);
        EmpPop.Information("emp_travel_handoff".lang());
    }

    /// <summary>
    ///     The host leaves us the map we stand on: its copy is the reference, not what stands on our screen (one
    ///     message missed earlier would become true for everyone: the others load our copy, and so does the host
    ///     when it comes back). Same numbers: nothing to do. Otherwise its copy is loaded under our feet
    /// </summary>
    /// <param name="from">who kept the map until now, for the journal: the host, or the player we were visiting
    ///     (its copy went to the host, which sent it on, see ElinNetHost.HandOverZone)</param>
    /// <returns>true when the map was loaded again</returns>
    private bool AdoptHostCopy(Zone zone, ZoneLeaseGrant grant, string from)
    {
        if (grant.Map is null || grant.MapSums is not { } theirs) {
            return false;
        }

        var ours = ZoneLeaseState.Sums(_map);
        if (ours.SequenceEqual(theirs)) {
            EmpLog.Information("Taking over {ZoneFullName} from {From}: our copy is the same, kept ({Sums})",
                zone.ZoneFullName, from, ZoneLeaseState.TellSums(ours));
            return false;
        }

        // only what the things on the floor hold differs: still theirs. Kept, our copy went back whole with our next
        // release and wrote over the chests of the world (lease audit 2026-10-29, #3)
        if (ZoneLeaseState.SameFloor(ours, theirs)) {
            EmpLog.Information("Taking over {ZoneFullName} from {From}: the content of a container differs, loading theirs (here {Local} | there {Host})",
                zone.ZoneFullName, from, ZoneLeaseState.TellSums(ours), ZoneLeaseState.TellSums(theirs));
        }

        // ponytail: what we did in the last round trip and the keeper never saw is not in its copy. A thing put
        // down then is lost (one picked up is taken off the floor below). Fix when seen: wait for an ack as a
        // leaving player does
        EmpLog.Warning("Taking over {ZoneFullName} from {From}: our copy differs, replaced by theirs (here {Local} | there {Host})",
            zone.ZoneFullName, from, ZoneLeaseState.TellSums(ours), ZoneLeaseState.TellSums(theirs));

        var stood = pc.pos.Copy();
        var carried = pc.things.Flatten().Select(t => t.uid).ToHashSet();

        // we stay on our tile: the game only moves a character that walks in, see Zone.AddGlobalCharasOnActivate
        pc.global.transition = null;

        // close the windows of the player before the map goes (a bag or a chest open on it): Scene.Init, which
        // player.MoveZone runs, does the same but after the unload. Not waited for: a fight going on or a
        // menu the player is in is cut short (known limit, the reload is not delayed until the player is idle)
        ui.RemoveLayers();

        try {
            // as OnZoneActivateResponse reloads the active map
            zone.Deactivate();

            // the game puts the artifacts lying here in our bag on the way out (Zone.Deactivate): they come back
            // with the host's map
            foreach (var thing in pc.things.Flatten().Where(t => !carried.Contains(t.uid)).ToList()) {
                thing.parentCard?.RemoveCard(thing);
            }

            zone.UnloadMap();
            ZoneLeaseState.WriteMap(zone, grant.Map);
        } catch (Exception ex) {
            // no map to stand on (disk, truncated map): back to the host, which sends us its own
            EmpLog.Warning(ex, "Taking over {ZoneFullName} from {From}: their copy could not be loaded, returning to the host",
                zone.ZoneFullName, from);

            // a visitor that no longer holds a good map must not hand it back as the zone's: it is a guest again
            if (Session.IsAway) {
                Session.IsGuest = true;
            }

            SendRejoin();
            return false;
        }

        player.MoveZone(zone);

        if (pc.isDead) {
            PutHimRightEr(stood);
        }

        // what we carry cannot lie on the floor too: picked up in the last round trip, that copy never saw it.
        // Kept where a player alone would have it, in the bag
        // Known limits, not handled (the uid is what is compared): a pick-up merged into a stack of the bag (the
        // floor thing is gone from our bag, the floor copy stays: doubled), a partial pick-up of a stack (same),
        // a thing taken from a chest of the received map (it is back in the chest)
        var doubled = _map.things.Where(t => carried.Contains(t.uid)).ToList();
        foreach (var thing in doubled) {
            zone.RemoveCard(thing);
            if (pc.things.Flatten().FirstOrDefault(t => t.uid == thing.uid) is { } kept) {
                CardCache.Set(kept);
            }
        }

        if (doubled.Count > 0) {
            EmpLog.Warning("Taking over {ZoneFullName}: {Count} thing(s) of that copy are in our bag already, taken off the floor: {Uids}",
                zone.ZoneFullName, doubled.Count, doubled.Select(t => t.uid));
        }

        return true;
    }

    /// <summary>
    ///     The host leaves the map we stand on and another player takes it over: we stay, as its guest.
    ///     Same path as asking to join a zone someone simulates, see OnZoneLeaseDepart
    /// </summary>
    private void StayAsGuest(ZoneLeaseGrant grant)
    {
        if (_pendingTravel is not null || _rejoining) {
            return;
        }

        WorldStateDeltaProcess();

        // the host hands our character to the one keeping this map and our game reloads from that player's world:
        // a thing held on the mouse (a stack just split) was in neither, lost (GitHub issue 48). Back in its bag,
        // and that reaches the host before our ack
        HeldDrag.PutBack("staying as the guest of the player keeping this map");
        FlushDeltasNow();

        _pendingTravel = (_zone, new ZoneTransition());
        _pendingGrant = grant;
        StopWorldStateUpdate();

        // our game reloads this very map from the one keeping it: we stay on our tile
        Host.Send(new ZoneLeaseAck {
            ZoneUid = grant.ZoneUid,
            Stood = pc.pos,
        });
    }

    /// <summary>
    ///     Net event: another player joins the zone we simulate, host it in a zone session
    /// </summary>
    private void OnZoneGuestRequest(ZoneGuestRequest request)
    {
        var accepted = Session.IsZoneAuthority && Session.AwayZone?.uid == request.ZoneUid &&
                       _zone == Session.AwayZone && _pendingTravel is null && !_rejoining;

        if (accepted) {
            var zoneHost = Session.ZoneSession as ElinNetHost ?? StartZoneSession();
            zoneHost.RegisterGuest(request);
        }

        EmpLog.Information("Guest {RemoteIdentity} for zone {ZoneUid}: {Accepted}",
            request.GuestUser, request.ZoneUid, accepted);

        Host.Send(new ZoneGuestReady {
            ZoneUid = request.ZoneUid,
            GuestUser = request.GuestUser,
            Accepted = accepted,
            Port = ElinNetHost.ZoneSessionPort,
            // (on a hidden server's account the zone session listens on virtual port 1, the server keeps 0)
            VirtualPort = Helper.HiddenServer.Playing ? ElinNetHost.ZoneSessionVirtualPort : 0,
            Local = IsLocalConnection && !Helper.HiddenServer.Playing,
        });
    }

    private ElinNetHost StartZoneSession()
    {
        var zoneHost = Session.InitializeZoneSession<ElinNetHost>();
        zoneHost.StartZoneServer(IsLocalConnection, Helper.HiddenServer.Playing);
        return zoneHost;
    }

    /// <summary>
    ///     Net event: Travel refused, stay where we are
    /// </summary>
    private void OnZoneLeaseDenied(ZoneLeaseDenied denied)
    {
        EmpLog.Information("Zone lease {ZoneUid} denied: {Reason}",
            denied.ZoneUid, denied.Reason);

        // not a refusal: the host comes along to our quest and runs its zone itself. The zone made here takes
        // the number of the host's (as a leased one does) and is the one its map lands in when we follow the
        // host there: it knows the quest, who gave it and where to go back to. What the host put in it comes
        // with the map, and with OnQuestZoneState
        if (denied.Reason == QuestFollowDelta.Coming) {
            if (_pendingTravel is { } asked && asked.Zone.IsInstance && _pendingGrant is null) {
                _pendingTravel = null;
                AdoptHostUid(asked.Zone, denied.ZoneUid);
            }

            return;
        }

        // out of the zone of a quest there is no staying: it is over, back to the host
        if (Session.IsAway && pc.currentZone?.IsInstance == true && _pendingGrant is null && !_rejoining) {
            // where the game sends it back to: the place the zone was entered from, in the town of the quest.
            // Only if the host stands in that town: elsewhere nothing tells where
            var back = _pendingTravel is { } refused ? ZoneArrival.Create(refused.Zone.uid, refused.Transition) : null;
            _pendingTravel = null;
            SendRejoin(back);
            return;
        }

        // the host stands there (we had not heard yet that it moved): going there is rejoining it
        if (denied.Reason == "emp_travel_host_zone" && Session.IsAway && _pendingGrant is null && !_rejoining) {
            var hostZoneUid = denied.ZoneUid;

            // host rule SoftRecall, slice 2: a zone we made ourselves (the floor the host already went down to,
            // made after our last copy of the world) takes the number the host knows it by, as a leased one does:
            // the map that comes without a world lands in it, and the way in is noted under the right number
            if (Session.Rules.SoftRecall && denied.HostZoneUid > 0 && _pendingTravel is { } made) {
                hostZoneUid = denied.HostZoneUid;

                // not when another zone of ours holds that number (ours are as old as our world): the map then
                // builds its own zone from what comes with it (OnZoneDataResponse)
                if (game.spatials.Find(hostZoneUid) is not { } other || other == made.Zone ||
                    other.ZoneFullName == made.Zone.ZoneFullName) {
                    AdoptHostUid(made.Zone, hostZoneUid);
                }
            }

            var arrival = _pendingTravel is { } going ? ZoneArrival.Create(hostZoneUid, going.Transition) : null;
            _pendingTravel = null;
            _hostZoneUid = hostZoneUid;
            SendRejoin(arrival);
            EmpPop.Debug("emp_travel_returning".lang());
            return;
        }

        _pendingTravel = null;
        EmpPop.Pop(denied.Reason.lang());

        // refused after leaving our place to join another player: back to the host
        if (_pendingGrant is not null) {
            _pendingGrant = null;
            SendRejoin();
        } else if (!Session.IsAway && _hostZoneAfterDeparture is not null && !_rejoining) {
            // the host changed map while we asked to go elsewhere (we did not follow it): we stay with it, on its map
            _hostZoneAfterDeparture = null;
            SendRejoin();
        }
    }

    /// <summary>
    ///     Net event: the host runs the zone of our quest, this is what its quest events hold there. Shown here
    ///     (how many monsters are left), and run from here if the host goes back alone and leaves us the zone
    /// </summary>
    internal void OnQuestZoneState(int zoneUid, LZ4Bytes? events)
    {
        if (events is null || game.spatials.Find(zoneUid) is not { IsInstance: true } zone) {
            return;
        }

        zone.events.list.RemoveAll(e => e is ZoneEventQuest);
        foreach (var zoneEvent in events.Decompress<List<ZoneEvent>>()) {
            // as a loaded one: not ZoneEventManager.Add, which starts it anew
            zoneEvent.zone = zone;
            zone.events.list.Add(zoneEvent);
        }
    }

    /// <summary>
    ///     Net event: The host wants to enter our zone, hand it back and rejoin the host
    /// </summary>
    private void OnZoneLeaseRecall(ZoneLeaseRecall recall)
    {
        if (Session.AwayZone?.uid != recall.ZoneUid) {
            // already handed back, or never held here: say so, the host would wait at the door of that map for good
            // (a release already on its way made the host forget the lease, this answer then does nothing)
            if (_pendingTravel is null && _pendingGrant is null) {
                Host.Send(new ZoneLeaseDecline {
                    ZoneUid = recall.ZoneUid,
                });
            }

            return;
        }

        if (_pendingTravel is not null) {
            // leaving already, the zone is released as soon as the next one is granted
            return;
        }

        // host rule SoftRecall: we keep our game and wait for ZoneSoftRejoin instead of the world, see
        // ElinNetClientSoftRejoin. Anything else going on (dead, visitors, not on that map): as before
        // (also a visitor of the player holding it, GitHub issue 46: it stays there with it)
        if (recall.Soft && (CanRejoinSoftly() || CanRejoinSoftlyAsGuest())) {
            _softRejoinDeadline = Time.realtimeSinceStartup + SoftRejoinWaitSeconds;
        }

        EmpLog.Information("Host recalls zone {ZoneFullName}, rejoining (in place {Soft})",
            Session.AwayZone.ZoneFullName, IsAwaitingSoftRejoin);

        SendRejoin();
        // the screen reloads when the host answers: say who is coming and why
        EmpPop.Information("emp_travel_recalled_by".lang(), Host);
    }

    /// <summary>
    ///     Host zone change while away, rejoin if the host arrived in our zone anyway
    /// </summary>
    private void OnHostZoneChangedWhileAway(int zoneUid)
    {
        _hostZoneUid = zoneUid;

        // the host is already out of the zone it asked us along to
        if (zoneUid != _questInviteZone) {
            CloseQuestInvite();
        }

        if (Session.AwayZone?.uid != zoneUid || _pendingTravel is not null) {
            return;
        }

        // the world map is never recalled: everyone walks its own copy (ElinNetHost.CanEnterNow), the host
        // stepping on its own is no reason to drop ours and load the whole world again, each time it leaves a
        // town. We keep walking where we are; to travel with the host, a player joins it from a map
        if (Session.AwayZone.IsRegion) {
            EmpLog.Debug("Host walks the world map too, we stay on our copy");
            return;
        }

        EmpLog.Warning("Host entered away zone {ZoneFullName} without recall, rejoining",
            Session.AwayZone.ZoneFullName);

        SendRejoin();
    }

    /// <summary>
    ///     How long the question "come along?" stays open, no answer is a no
    /// </summary>
    private const float QuestInviteSeconds = 15f;

    private Dialog? _questInvite;
    private float _questInviteDeadline;
    private int _questInviteZone = -1;

    /// <summary>
    ///     Net event: the host entered the zone of a quest it took and left us in town, it asks us along
    /// </summary>
    internal void OnQuestFollowInvite(QuestFollowDelta invite)
    {
        // only a player away in that town (keeping it or visiting it), with nothing else going on: no quest zone
        // of its own, no trade
        if (!PersonalQuests.InstancesEnabled || !Session.IsAway || IsInTransfer || _pendingTravel is not null ||
            _questInviteDeadline > 0 || invite.ZoneUid != _hostZoneUid ||
            game.quests.list.Any(q => q.UseInstanceZone && PersonalQuests.IsPersonal(q)) ||
            PlayerTrade.View is { Phase: PlayerTrade.Invited or PlayerTrade.Open }) {
            EmpLog.Debug("Not asking to follow the host to quest zone {ZoneUid}", invite.ZoneUid);
            return;
        }

        _questInviteZone = invite.ZoneUid;
        _questInviteDeadline = Time.realtimeSinceStartup + QuestInviteSeconds;
        _questInvite = Dialog.YesNo("emp_quest_follow_ask".Loc(invite.Name),
            FollowHost,
            () => AnswerQuestInvite(QuestFollowDelta.Declined));
    }

    private void UpdateQuestInvite()
    {
        if (_questInviteDeadline <= 0) {
            return;
        }

        // we moved on meanwhile (recalled, travelling): the question is void
        if (!Session.IsAway || IsInTransfer || _pendingTravel is not null) {
            CloseQuestInvite();
            return;
        }

        // closed without a click
        if (_questInvite == null) {
            AnswerQuestInvite(QuestFollowDelta.Declined);
            return;
        }

        if (Time.realtimeSinceStartup >= _questInviteDeadline) {
            CloseQuestInvite();
            AnswerQuestInvite(QuestFollowDelta.NoAnswer);
        }
    }

    private void AnswerQuestInvite(int kind)
    {
        _questInviteDeadline = 0;
        _questInvite = null;

        SendWhileAway(new QuestFollowDelta {
            Kind = kind,
            Name = pc.Name,
        });
    }

    private void CloseQuestInvite()
    {
        if (_questInviteDeadline <= 0) {
            return;
        }

        _questInviteDeadline = 0;
        if (_questInvite != null) {
            _questInvite.Close();
        }

        _questInvite = null;
    }

    /// <summary>
    ///     Join the host where it is now (the zone of its quest): the map we kept goes back to it, then the same
    ///     way in as coming back to the host's map. The quest stays the host's, see QuestZoneVisitorPatch
    /// </summary>
    internal void FollowHost()
    {
        _questInviteDeadline = 0;
        _questInvite = null;

        // a visitor leaves the same way as when the host recalls it (OnZoneLeaseRecall)
        if (!Session.IsAway || _pendingTravel is not null || IsInTransfer) {
            return;
        }

        EmpLog.Information("Following the host from {AwayZone} to its zone {ZoneUid}",
            Session.AwayZone!.ZoneFullName, _hostZoneUid);

        SendRejoin();
        EmpPop.Debug("emp_travel_returning".lang());
    }

    /// <summary>
    ///     Hand the away zone back and return to the host <br />
    ///     The host answers with a save probe, which rebuilds the game in the host zone
    /// </summary>
    /// <param name="arrival">we walk into the host's map: the way in. Not when the host is the one coming to us</param>
    private void SendRejoin(ZoneArrival? arrival = null)
    {
        if (_rejoining) {
            return;
        }

        // host rule SoftRecall, slice 2: we walk into the host's map and keep our game. The release is marked
        // Soft (CreateLeaseRelease) and the host answers ZoneSoftRejoin then that one map, or the world as before
        if (arrival is not null && CanReturnSoftly(arrival.ZoneUid)) {
            _softReturnZone = arrival.ZoneUid;
            _softRejoinDeadline = Time.realtimeSinceStartup + SoftRejoinWaitSeconds;
        }

        _rejoining = true;
        HandBackAwayZone(true, arrival);
    }

    /// <summary>
    ///     Leaving the zone: hand it back (our guests stay, the host gives it to one of them, see HandOverZone)
    /// </summary>
    private const float TransferLockTimeout = 30f;

    private float _transferLockedAt;
    private bool _transferLocked;

    /// <summary>
    ///     Between the moment our character and bag were handed over (leaving the host map, coming back to it)
    ///     and the moment the next world is here. What is done in that window is known to nobody: an item
    ///     picked up would exist twice, one dropped would be lost
    /// </summary>
    /// <remarks>
    ///     Also while a guest waits to hear what becomes of the map its owner just left: if the host recalls it,
    ///     the copy the owner handed back is the one kept
    /// </remarks>
    internal bool IsInTransfer => _pendingGrant is not null || _rejoining || _handoffDeadline > 0 || _softReturnZone != 0 ||
                                  IsAwaitingSoftStay;

    /// <summary>
    ///     No input during a transfer, it lasts a round trip
    /// </summary>
    private void UpdateTransferLock()
    {
        if (!IsInTransfer) {
            if (_transferLocked) {
                _transferLocked = false;
                EInput.haltInput = false;
            }

            return;
        }

        if (!_transferLocked) {
            _transferLocked = true;
            _transferLockedAt = Time.realtimeSinceStartup;
        }

        // never for good: a transfer that does not end is a bug, not a reason to freeze the player
        if (Time.realtimeSinceStartup - _transferLockedAt > TransferLockTimeout) {
            EInput.haltInput = false;
            return;
        }

        EInput.haltInput = true;
    }

    /// <summary>
    ///     A travel whose answer never comes (the host threw in the middle of it, the player we go to went quiet) left
    ///     this game waiting for good, with no checkpoint meanwhile (reliability audit 2026-10-27, #3). After that
    ///     long it comes back to the host the way every return does; a return that gets no world either joins the
    ///     game again, as a lost link does
    /// </summary>
    private const float TravelStuckSeconds = 45f;

    // the world of a return is the biggest message there is: more time for it
    private const float RejoinStuckSeconds = 90f;

    private const float RejoinMaxSeconds = 600f;

    private float _travelSince;
    private float _rejoinSince;
    private long _rejoinBytes;

    private void UpdateTravelWatchdog()
    {
        // the waits that have a deadline of their own are left to it (a zone handed over, a soft return, a world asked)
        var waiting = (_pendingTravel is not null || _pendingGrant is not null || _rejoining) && _handoffDeadline <= 0 &&
                      !IsAwaitingSoftRejoin && _softReturnZone == 0 && _worldAskDeadline <= 0 && IsConnected;
        var now = Time.realtimeSinceStartup;
        if (!waiting) {
            _travelSince = 0;
            _rejoinSince = 0;
            return;
        }

        if (_travelSince <= 0) {
            _travelSince = now;
            return;
        }

        if (!_rejoining && now - _travelSince >= TravelStuckSeconds) {
            EmpLog.Warning("Travel to {ZoneUid} got no answer for {Seconds:F0}s (lease {Granted}), returning to the host",
                _pendingTravel?.Zone.uid ?? _pendingGrant?.ZoneUid ?? -1, now - _travelSince, _pendingGrant is not null);
            _pendingTravel = null;
            _pendingGrant = null;
            _travelSince = now;
            SendRejoin();
            EmpPop.Debug("emp_travel_returning".lang());
            return;
        }

        // silence, not total time: a big world arriving piece by piece over a slow link is not stuck (pieces of big
        // messages only: the lean snapshots a departed guest still gets every tick would reset it forever, bench
        // travel_watchdog_check 2026-10-28)
        // (up to RejoinMaxSeconds in all: a host that keeps talking but never sends the world is stuck too)
        if (!_rejoining) {
            _rejoinSince = 0;
        } else if (_rejoinSince <= 0) {
            _rejoinSince = now;
        }

        if (_rejoining && now - _rejoinSince < RejoinMaxSeconds && NetFragments.PiecesReceived != _rejoinBytes) {
            _rejoinBytes = NetFragments.PiecesReceived;
            _travelSince = now;
        }

        if (_rejoining && now - _travelSince >= RejoinStuckSeconds) {
            EmpLog.Warning("No world {Seconds:F0}s after handing back our place, joining the game again", now - _travelSince);
            _travelSince = 0;
            if (Host is not null) {
                Socket.Disconnect(Host, EmpDisconnectInfo.RemoteClosed);
            }
        }
    }

    private void HandBackAwayZone(bool rejoin, ZoneArrival? arrival = null)
    {
        // our character goes with the release: what is held on the mouse first back in its bag (issue 48)
        HeldDrag.PutBack(rejoin ? "going back to the host" : "handing back our zone");

        Host.Send(CreateLeaseRelease(rejoin, arrival: arrival));

        if (Session.ZoneSession is ElinNetHost) {
            Session.RemoveZoneSession();
        }
    }

    private void EnterAway(Zone zone)
    {
        var wasAway = Session.IsAway;

        // from now on the game runs as single player, see NetSession.Connection
        Session.AwayZone = zone;

        if (wasAway) {
            return;
        }

        _skippedAway.Clear();
        _awaySince = Time.realtimeSinceStartup;

        // the host may already have told us where it went (it left the map we stay on)
        _hostZoneUid = _hostZoneAfterDeparture ?? Session.CurrentZone?.uid ?? -1;
        _hostZoneAfterDeparture = null;
        _nextCheckpoint = Time.realtimeSinceStartup + Session.Rules.TravelCheckpointSeconds;
        StopWorldStateUpdate();
        Delta.ClearOut();
        Delta.ClearIn();
        EmptyWorldContainers();

        // other players stay with the host (party lists may hold empty slots)
        foreach (var member in pc.party?.members.ToList() ?? []) {
            if (member is not null && member != pc && Session.CurrentPlayers.Any(p => p?.CharaUid == member.uid)) {
                pc.party!.RemoveMember(member);
            }
        }
    }

    private void FlushDeltasNow()
    {
        Delta.RefreshBuffer();

        // second pass sends what was deferred by the first
        for (var i = 0; i < 3 && Delta.HasPendingOut; i++) {
            if (Delta.FlushOutBuffer() is { Count: > 0 } deltaList) {
                Host.Send(new WorldStateDeltaList {
                    DeltaList = deltaList,
                });
            }
        }
    }

    private ZoneLeaseRelease CreateLeaseRelease(bool rejoin, bool checkpoint = false, ZoneArrival? arrival = null)
    {
        // only a map we really stand on: on our way to one, our tile is still the one of the map we left
        var stoodIn = Session.AwayZone is { } away && _zone == away ? away.uid : -1;

        if (!Session.IsZoneAuthority || Session.AwayZone is not { } zone) {
            // a guest (or a player refused on its way) holds no zone, it only brings its character
            return new() {
                ZoneUid = -1,
                ZoneState = [],
                Chara = LZ4Bytes.Create(pc),
                Companions = CollectCompanions(),
                Residents = Patches.AwayResidents.Collect(!checkpoint),
                GoneResidents = Patches.AwayResidents.CollectGone(!checkpoint),
                UidNext = game.cards.uidNext,
                Rejoin = rejoin,
                Checkpoint = checkpoint,
                StoodZoneUid = stoodIn,
                Arrival = arrival,
                // a visitor staying on the map the host walks onto (GitHub issue 46), see CanRejoinSoftlyAsGuest
                Soft = rejoin && !checkpoint && arrival is null && IsAwaitingSoftRejoin,
            };
        }

        // the world map is a local copy for everyone; the zone of a quest is gone once left
        var map = zone.IsRegion || zone.IsInstance ? null : ZoneLeaseState.CollectMap(zone);
        // (taken before the branch is packed: what the host pays is not in it any more)
        var income = Helper.LeasedBase.TakeIncome(zone);

        return new() {
            Branch = Helper.LeasedBase.Pack(zone),
            Name = Helper.LeasedBase.Name(zone),
            BaseElements = Helper.LeasedBase.Elements(zone),
            Income = income,
            ZoneUid = zone.uid,
            ZoneState = ZoneLeaseState.GetState(zone),
            IdCurrentSubset = zone.idCurrentSubset,
            FortuneRoll = ZoneLeaseState.GetFortuneRoll(zone),
            Map = map,
            // leaving for good: a visitor who takes the zone over compares its copy with this one
            MapSums = map is null || checkpoint || zone.map is null ? null : ZoneLeaseState.Sums(zone.map),
            Chara = LZ4Bytes.Create(pc),
            Companions = CollectCompanions(),
            Residents = Patches.AwayResidents.Collect(!checkpoint),
            GoneResidents = Patches.AwayResidents.CollectGone(!checkpoint),
            UidNext = game.cards.uidNext,
            Rejoin = rejoin,
            Checkpoint = checkpoint,
            StoodZoneUid = stoodIn,
            Arrival = arrival,
            Soft = rejoin && !checkpoint && IsAwaitingSoftRejoin,
            GuestCharas = (Session.ZoneSession as ElinNetHost)?.CollectGuestCharas(),
            GuestCompanions = (Session.ZoneSession as ElinNetHost)?.CollectGuestCompanions(),
        };
    }

    private static List<LZ4Bytes> CollectCompanions()
    {
        return CompanionHelper.TravellingWith(pc).Select(c => LZ4Bytes.Create(c)).ToList();
    }

    internal void SendChatWhileAway(MsgSayDelta delta)
    {
        SendWhileAway(delta);
    }

    /// <summary>
    ///     What an away player still shares with the world: chat, the quest log
    /// </summary>
    internal void SendWhileAway(ElinDelta delta)
    {
        Host.Send(new WorldStateDeltaList {
            DeltaList = [delta],
        });
    }

    /// <summary>
    ///     What a player away from the host map takes of its deltas (the host sends it nothing else, see
    ///     ElinNetHost.SendByMap)
    /// </summary>
    internal static bool ReachesAwayPlayer(ElinDelta delta)
    {
        // (SpatialGenDelta: a zone the world made, a new nefia on the world map, known to an away player too, 2026-10-28;
        // AchievementDelta: one it earned; BaseRelocateDelta: a base moved on the world map, read there)
        return delta is MsgSayDelta or WhisperDelta or GateItemAnswer or GateZoneDelta or MarriageDelta or QuestStartDelta or QuestCompleteDelta or QuestChangePhaseDelta or DialogFlagDelta or QuestFailDelta or QuestUpdateDelta or PersonalStateDelta or PlayerStandingDelta or WorldDateAdvanceDelta or WorldMapTogetherDelta or PlayerLeadDelta or WeatherDelta or DayDataDelta or QuestFollowDelta or SleepReadyDelta or SleepStartDelta or CharaSleepDelta or BillPayDelta or CodexDelta or AddRecipeDelta or BossWinDelta or ExtraTaxDelta or QuestJournalDelta or TentZoneDelta or TeamsDelta or TeamLoreDelta or StoryBundleDelta or ModerationDelta or AdventurerBoutDelta or DuelStateDelta or SpatialGenDelta or AchievementDelta or BaseRelocateDelta or TeamEvictDelta or SiblingLandDelta or ClaimZoneDelta or MailStateDelta or ServerRosterDelta or GodGiftDelta or
               // its own revive at a bartender, from the host's game (GitHub issue 70: it stayed dead in its game)
               CharaReviveDelta;
    }

    /// <summary>
    ///     Delta lists reaching an away client are about the host map, except chat
    /// </summary>
    private void ApplyChatWhileAway(WorldStateDeltaList response)
    {
        foreach (var delta in response.DeltaList) {
            // chat and the quest log are the world's, the rest is about the host map
            if (ReachesAwayPlayer(delta)) {
                // the regular delta loop does not run while away, see CoreSynchronizationContext
                delta.Apply(this);
            } else {
                var name = delta.GetType().Name;
                _skippedAway[name] = _skippedAway.GetValueOrDefault(name) + 1;
            }
        }
    }

    // measure only (council 11): what the host said while this player was away and that its game did not take,
    // by kind, and since when. Read at the world copy that brings it back, see ReportReturn
    private readonly Dictionary<string, int> _skippedAway = [];
    private float _awaySince;

    /// <summary>
    ///     One line a return: how long away, how big the world copy, what was not taken meanwhile. The figures a
    ///     return without a world copy needs before it is written (how often nothing of the world moved)
    /// </summary>
    private void ReportReturn(int worldBytes)
    {
        var kinds = string.Join(", ", _skippedAway.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key} {k.Value}"));
        EmpLog.Information("Back after {Seconds:F0}s away: world copy of {Bytes} bytes, {Skipped} host deltas not taken meanwhile ({Kinds})",
            _awaySince > 0 ? Time.realtimeSinceStartup - _awaySince : 0f, worldBytes, _skippedAway.Values.Sum(), kinds);
        _skippedAway.Clear();
        _awaySince = 0;
    }

    /// <summary>
    ///     Regular progress save while away, so a disconnect only loses what happened since
    /// </summary>
    private void UpdateTravelCheckpoint()
    {
        var interval = Session.Rules.TravelCheckpointSeconds;
        if (!Session.IsZoneAuthority || interval <= 0 || _pendingTravel is not null || _rejoining ||
            !core.IsGameStarted || _zone != Session.AwayZone) {
            return;
        }

        if (Time.realtimeSinceStartup < _nextCheckpoint) {
            return;
        }

        _nextCheckpoint = Time.realtimeSinceStartup + interval;
        SendTravelCheckpoint();
    }

    internal void SendTravelCheckpoint()
    {
        Host.Send(CreateLeaseRelease(false, true));
        EmpLog.Debug("Sent checkpoint of zone {ZoneFullName}", Session.AwayZone!.ZoneFullName);
    }

    /// <summary>
    ///     Only lease traffic and session control reach an away client, the rest is about the host map
    /// </summary>
    private static bool ShouldReceiveWhileAway(object packet)
    {
        return packet is ZoneLeaseGrant or
            WorldStateDeltaList or
            ZoneLeaseDepart or
            ZoneLeaseDenied or
            ZoneLeaseRecall or
            ZoneSoftRejoin or
            ZoneGuestRequest or
            ShippingPayout or
            ZoneDataResponse or
            SaveDataProbe or
            NetSessionRules or
            WorldCopyManifest or
            WorldCopyPiece or
            HostLeaving or
            NetIntegrityRejected;
        // not SessionPlayersSnapshot: the players of a zone session are ours, the host list is about its map
    }
}
