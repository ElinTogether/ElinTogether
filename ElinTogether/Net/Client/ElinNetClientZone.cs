using System;
using ElinTogether.Common;
using ElinTogether.Models;
using ElinTogether.Patches;
using EModding.Helper.Runtime.Exceptions;
using ReflexCLI.Attributes;
using Serilog.Context;

namespace ElinTogether.Net;

[ConsoleCommandClassCustomizer("emp")]
internal partial class ElinNetClient
{
    private const int MaxZoneSyncRetries = 3;

    private int _zoneSyncFailures;

    /// <summary>
    ///     Request a map snapshot manually, mostly just used when joining a session
    /// </summary>
    public void RequestZoneState(MapDataRequest request)
    {
        EmpLog.Information("Requesting zone state {@Zone} from host",
            request);

        Host.Send(request);
    }

    private const float MapWaitSeconds = 15f;

    // changed by each world copy and each map received: a wait started before that is over
    private int _mapAwaited;

    /// <summary>
    ///     The host sends its map right behind the world. Should none have come after
    ///     <see cref="MapWaitSeconds" />, it is asked for once, as a joining player always did, instead of
    ///     waiting on the loading screen for good. A map that crosses this request is either refused as received
    ///     twice or loaded on top of the first, see <see cref="ElinDeltaManager.HoldForIncomingMap" />
    /// </summary>
    private System.Collections.IEnumerator AskMissingMap(int awaited)
    {
        var until = UnityEngine.Time.unscaledTime + MapWaitSeconds;
        while (UnityEngine.Time.unscaledTime < until) {
            yield return null;
        }

        if (awaited != _mapAwaited || !IsConnected) {
            yield break;
        }

        EmpLog.Warning("No map {Seconds:F0}s after the world copy, asking the host for it once",
            MapWaitSeconds);

        RequestZoneState(MapDataRequest.CurrentRemoteZone);
    }

    private void RetryZoneSync()
    {
        // council 11, slice 2: the map we walk into without a copy of the world cannot be loaded in the world we
        // have (a zone the host made since, whose parent is unknown here too): the world then, at once
        if (_softReturnZone != 0) {
            AskWorldCopy(_softReturnZone, "the host's map cannot be loaded in this world");
            return;
        }

        if (++_zoneSyncFailures >= MaxZoneSyncRetries) {
            // nobody is thrown out for that: it comes back as a player who just arrives, the world then its map.
            // The link is closed as a lost one, not with InvalidZone: that reason reads as "turned away" and
            // stops the automatic return (OnPeerDisconnected, NetReconnect)
            EmpLog.Warning("Zone sync failed after {RetryCount} attempts, joining the game again for its world and map",
                _zoneSyncFailures);

            _zoneSyncFailures = 0;
            Socket.Disconnect(Host, EmpDisconnectInfo.RemoteClosed);
            return;
        }

        EmpLog.Warning("Zone state unresolved, re-requesting current zone from host (attempt {RetryCount})",
            _zoneSyncFailures);

        RequestZoneState(MapDataRequest.CurrentRemoteZone);
    }

    /// <summary>
    ///     Whose land this is, as the host has it. A zone this game already knows keeps its own copy of the zone,
    ///     and only a world copy brought a change of owner: a land claimed during the session (by the host, or
    ///     asked by a guest, see ClaimZoneDelta) stayed "nobody's, no base" here, even when coming back to it
    ///     (2026-10-13: Vernis claimed by the host, still Palmia's for the guest). What a base holds once it
    ///     exists is told piece by piece, as before
    /// </summary>
    private static void AdoptOwner(Zone zone, ZoneDataResponse response)
    {
        try {
            var hosts = response.Zone.Decompress<Zone>();
            if (hosts.idMainFaction == zone.idMainFaction && hosts.branch is null == zone.branch is null) {
                return;
            }

            EmpLog.Information("Zone {ZoneFullName}: owner {Owner} (base {HasBase}) here, {HostOwner} (base {HostHasBase}) for the host: taken from the host",
                zone.ZoneFullName, zone.idMainFaction, zone.branch is not null, hosts.idMainFaction, hosts.branch is not null);
            zone.idMainFaction = hosts.idMainFaction;
            zone.branch = hosts.branch;
            zone.branch?.SetOwner(zone);
            zone.icon = hosts.icon;
            zone.idPrefix = hosts.idPrefix;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Owner of zone {ZoneFullName} could not be read from the host's state", zone.ZoneFullName);
        }
    }

    /// <summary>
    ///     Net event: Received zone state update, must do a scene init
    /// </summary>
    private void OnZoneDataResponse(ZoneDataResponse response)
    {
        using var _ = LogContext.PushProperty("Zone", new { response.ZoneFullName, response.ZoneUid }, true);

        if (Session.IsAway && !IsZoneSession) {
            // host changed zone while we are away, only remember where to find it
            EmpLog.Debug("Host moved to {ZoneFullName} while away", response.ZoneFullName);
            OnHostZoneChangedWhileAway(response.ZoneUid);
            return;
        }

        // (also when our own move is only asked: the host changing map at that moment pulled us along to its new
        // map instead of where we were going, a concert of our quest given up for the host's next floor, reported
        // 2026-10-14. Asked and refused, we go to it then, see OnZoneLeaseDenied)
        if (!IsZoneSession && !Session.IsAway && (IsAwaitingDeparture || _pendingTravel is not null)) {
            // we are leaving its map (it left, we stay behind): do not follow it
            EmpLog.Debug("Host moved to {ZoneFullName} while we stay behind", response.ZoneFullName);
            _hostZoneAfterDeparture = response.ZoneUid;
            return;
        }

        // the host answers our request and tells everyone when it enters a map: coming back to it, both land
        // moments apart for the same map. The first one is being loaded, a second load would only replay it
        if (_awaitingActivation == response.ZoneUid && UnityEngine.Time.unscaledTime - _awaitingActivationSince < ActivationWait) {
            EmpLog.Debug("Zone state of {ZoneFullName} received twice, keeping the first", response.ZoneFullName);
            return;
        }

        EmpLog.Information("Received zone state");

        // a map follows after all: the placement that comes with it is a real one
        _softPlaced = 0;

        // the map awaited since the world copy, see AskMissingMap
        _mapAwaited++;

        Delta.HoldForIncomingMap();
        response.WriteToTemp();

        var spatial = game.spatials;

        // popping from spatial gen refs makes no sense actually
        var remoteZone = response.FindZone();
        if (remoteZone is not null && remoteZone.ZoneFullName != response.ZoneFullName) {
            // uid registered but clients don't really hold uid anyway
            EmpLog.Warning("Zone uid {ZoneUid} occupied by divergent local zone {LocalZoneFullName}, " +
                           "replacing with host {ZoneFullName}",
                response.ZoneUid, remoteZone.ZoneFullName, response.ZoneFullName);
            remoteZone = null;
        }

        if (remoteZone is null) {
            EmpLog.Information("Remote zone does not exist, waiting for new spatial gen");

            try {
                var probeZone = response.Zone.Decompress<Zone>();
                var parent = spatial.Find(probeZone.parent.uid);

                if (parent is null) {
                    EmpLog.Warning("Remote zone parent does not exist in current game");

                    RetryZoneSync();
                    return;
                }

                // swap out the serialized parent
                probeZone.parent = parent;
                spatial.map[response.ZoneUid] = remoteZone = probeZone;

                // the zone of a quest sits on the tile of its town without taking its place on the map
                if (parent is Region region && !probeZone.IsInstance) {
                    region.elomap.SetZone(probeZone.x, probeZone.y, remoteZone, true);
                }
            } catch (Exception ex) {
                // was silent in a release build: all that showed was "Zone state mismatch" below
                EmpLog.Warning(ex, "Zone {ZoneFullName} is unknown here and could not be built from its state",
                    response.ZoneFullName);
                DebugThrow.Void(ex);
            }
        }

        if (remoteZone?.ZoneFullName != response.ZoneFullName) {
            EmpLog.Warning("Zone state mismatch");

            RetryZoneSync();
            return;
        }

        _zoneSyncFailures = 0;

        // council 11: with the host rule SoftRecall this world is no longer copied at each return, and what it
        // holds about a zone is as old as its last copy (or made here, for a zone we created). The host's comes
        // with its map: a zone we keep later (TakeOverZone) hands these numbers back to the host with our release
        if (Session.Rules.SoftRecall && response.ZoneState is { } state) {
            ZoneLeaseState.ApplyState(remoteZone, state, response.IdCurrentSubset);
        }

        AdoptOwner(remoteZone, response);

        // suppress client-side map regeneration
        remoteZone.isGenerated = true;
        remoteZone.dateExpire = int.MaxValue;
        remoteZone.dateRegenerate = int.MaxValue;

        if (remoteZone is Region eloMap) {
            // suppress client-side overworld poi generation
            eloMap.dateCheckSites = int.MaxValue;
        }

        // update session remote zone
        Session.CurrentZone = remoteZone;

        _awaitingActivation = response.ZoneUid;
        _awaitingActivationSince = UnityEngine.Time.unscaledTime;

        // respond for replication complete, waiting for position sync
        Host.Send(response.Ready());
    }

    private const float ActivationWait = 10f;

    /// <summary>
    ///     The map this game stood on when a copy of the world came (OnSaveDataProbe), 0 if none (GitHub issue 46)
    /// </summary>
    private int _coverZone;

    private const float InPlaceCoverSeconds = 0.2f;

    /// <summary>
    ///     The map whose state we loaded and told the host about, until the host says where we stand on it
    /// </summary>
    private int _awaitingActivation;
    private float _awaitingActivationSince;

    /// <summary>
    ///     Net event: Ready to init scene with new zone state and sync position
    /// </summary>
    private void OnZoneActivateResponse(ZoneActivateResponse response)
    {
        using var _ = LogContext.PushProperty("Zone", new { response.ZoneFullName, response.ZoneUid }, true);

        _awaitingActivation = 0;

        // council 11: the world was asked for instead (AskWorldCopy), a placement that crossed it is void
        if (_worldAskDeadline > 0) {
            EmpLog.Debug("Ignoring the placement on {ZoneUid}, the world is on its way", response.ZoneUid);
            return;
        }

        // council 11: we never left that map nor loaded it again, the host only says where it holds us
        if (_softPlaced != 0 && _softPlaced == response.ZoneUid) {
            _softPlaced = 0;
            ConfirmSoftPlacement(response);
            return;
        }

        EmpLog.Information("Received zone activation");

        var currentZone = Session.CurrentZone;
        Point? ownSpot = null;

        if (currentZone?.uid != response.ZoneUid) {
            EmpLog.Debug("Ignoring stale ZoneActivateResponse (expected {ExpectedZoneFullName}/{ExpectedZoneUid}, " +
                         "got {GotZoneFullName}/{GotZoneUid})",
                currentZone?.ZoneFullName, currentZone?.uid, response.ZoneFullName, response.ZoneUid);
            return;
        }

        // a map sent by the game that runs it: someone is there, see BossFleePatch
        ZoneLeaseState.Imported.Add(currentZone.uid);

        if (player.zone is null) {
            // first time joining, need to do scene init from title
            EmpLog.Debug("Starting initial scene init");

            player.zone = pc.currentZone = currentZone;

            // the place the host gives on this map, before the map is shown: the character of the world copy can
            // still stand where it was on another map (the host went down a floor and came back while the copy
            // was on its way), outside this one: every read of its cell then threw, the game was left half started
            if (response.Pos is { X: >= 0, Z: >= 0 } given) {
                pc.pos.Set(given.X, given.Z);
            }

            BringRideAlong();

            // GitHub issue 46: a player who stayed where it stood (the host left that map and another player keeps
            // it, or the host walked onto it) only had its world replaced: Scene.Init would fade the cover out over
            // 4 s (HideCover(4f), meant for a first join from the title), a black screen for a player who did not
            // move. Started here, that call is ignored (UI.HideCover runs once while hiding)
            if (_coverZone != 0 && _coverZone == currentZone.uid) {
                ui.HideCover(InPlaceCoverSeconds);
            }

            _coverZone = 0;

            try {
                scene.Init(Scene.Mode.Zone);
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Scene init failed on {ZoneUid} with the character at {@Pos}, placing it and asking for the map again",
                    response.ZoneUid, (Position)pc.pos);
                if (_map is { } loaded && (pc.pos.x < 0 || pc.pos.z < 0 || pc.pos.x >= loaded.Size || pc.pos.z >= loaded.Size)) {
                    var center = loaded.GetCenterPos();
                    pc.pos.Set(center.x, center.z);
                }

                BringRideAlong();

                RetryZoneSync();
                return;
            }

            // as Game.Load does after its own Scene.Init: the windows that were open before the world was replaced
            Helper.OwnSettings.Reopen();
            // the first time in this world with this character: the three first steps (once, remembered)
            Helper.FirstSteps.AfterJoin();

            // the world copy of a guest walking in from its own world map: Player.MoveZone, which pays the journey on
            // arrival in a town, runs only on the other entries (EnterHostMap), never here (hunt 6 item 3, H603)
            OwnerPartyPatch.PayTravel(currentZone);
        } else if (_softReturnZone != 0) {
            // council 11, slice 2: the first map since we came back without a copy of the world
            if (!EnterBySoftReturn(currentZone)) {
                return;
            }
        } else {
            ownSpot = TakeResyncSpot(currentZone);
            EnterHostMap(currentZone);
        }

        // the map is loaded: what the host did on it since it took the copy is replayed now
        Delta.MapPlaced();

        // reassign zone pos
        this.StartDeferredCoroutine(() => {
            // zone state may be stale
            var fresh = Session.CurrentZone?.uid == response.ZoneUid && _zone == Session.CurrentZone;

            if (pc.isDead) {
                PutHimRightEr(fresh ? response.Pos : null);
                return;
            }

            if (!fresh || !response.Pos.IsInActiveMapBounds) {
                EmpLog.Debug("Skipping stale zone position sync (expected {ExpectedZoneUid}, got {GotZoneUid})",
                    _zone?.uid, response.ZoneUid);
                return;
            }

            // a map reloaded to repair it: where we stand, not where the host's copy of us stood (NetDesync)
            if (!MoveToResyncSpot(ownSpot)) {
                pc.Place(response.Pos);
            }

            pc.SetDir(pc.dir);
        });
    }

    /// <summary>
    ///     A mount, and what rides our character, stand on its tile (Chara.SyncRide). Our character was just put on
    ///     the tile the host gives: left on the tile of the map the world copy was taken on, they made the game
    ///     move them from a cell outside this map when it starts (Zone.Activate, SyncRide, Map._RemoveCard:
    ///     IndexOutOfRange), and the player could not join. Real session of 2026-10-08, a mounted player, 0.26.597
    /// </summary>
    private static void BringRideAlong()
    {
        foreach (var rider in new[] { pc.ride, pc.parasite, pc.host }) {
            if (rider is not null && !rider.pos.Equals(pc.pos)) {
                EmpLog.Debug("Bringing {Uid} from {@From} to our character's tile {@To}", rider.uid, (Position)rider.pos, (Position)pc.pos);
                rider.pos.Set(pc.pos.x, pc.pos.z);
            }
        }
    }

    /// <summary>
    ///     The game runs and the host sent a map: the one on screen is left (or loaded again when it is the same)
    ///     and our character stands in the host's zone. Its tile comes right after, see OnZoneActivateResponse
    /// </summary>
    private void EnterHostMap(Zone currentZone)
    {
        if (_zone == currentZone) {
            EmpLog.Debug("Reloading active zone from received snapshot");

            currentZone.Deactivate();
        }

        if (currentZone.IsLoaded) {
            currentZone.UnloadMap();
        }

        if (pc.currentZone != currentZone) {
            if (pc.isDead) {
                // he is but a husk
                currentZone.AddCard(pc);
            } else {
                pc.MoveZone(currentZone);
            }

            if (pc.currentZone != currentZone) {
                currentZone.AddCard(pc);
            }
        }

        player.MoveZone(currentZone);
    }

    private static void PutHimRightEr(Position? requested)
    {
        if (!pc.isDead || _zone is null || _map is null) {
            return;
        }

        Point pos = requested is { IsInActiveMapBounds: true }
            ? requested
            : _map.GetCenterPos();

        pc.pos.Set(pos.x, pos.z);
        if (!_map.charas.Contains(pc)) {
            _zone.AddCard(pc, pc.pos);
        }

        player.deathDialog = false;

        EmpLog.Debug("Restored dead pc at {@Pos} in zone {ZoneUid}",
            pc.pos, _zone.uid);
    }
}