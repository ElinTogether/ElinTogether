using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Patches;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Council 11, host rule SoftRecall: the host walks onto the map we hold alone. We hand the map and our
///     character back as always, but keep our game and our scene, and become a client of the host again in place:
///     the mirror of TakeOverZone / EnterAway. See ElinNetHost.CompleteSoftRejoin for the other side. <br />
///     Whatever does not go as expected ends with the copy of the world, as without the rule (AskWorldCopy)
/// </summary>
internal partial class ElinNetClient
{
    /// <summary>
    ///     How long we wait for <see cref="ZoneSoftRejoin" /> (or the world) after a release marked Soft: the
    ///     host saves, walks in and loads the map first
    /// </summary>
    private const float SoftRejoinWaitSeconds = 20f;

    private float _softRejoinDeadline;

    /// <summary>
    ///     The world was asked for (AskWorldCopy): should it never come, the link is closed as a lost one and the
    ///     game joins again by itself, as RetryZoneSync does
    /// </summary>
    private const float WorldAskWaitSeconds = 20f;

    private float _worldAskDeadline;

    /// <summary>
    ///     The map we stayed on: the placement the host sends next for it only confirms our tile, no map follows
    /// </summary>
    private int _softPlaced;

    /// <summary>
    ///     Our character and our map are with the host, its answer is not here yet: nothing moves in this game
    ///     meanwhile (see PauseGame), what would happen now is in neither copy
    /// </summary>
    internal bool IsAwaitingSoftRejoin => _softRejoinDeadline > 0;

    /// <summary>
    ///     Slice 2: the map of the host we walk into without a copy of the world (the number the host knows it
    ///     by), from our release to the moment that map is loaded here; 0 otherwise. Until then nothing moves in
    ///     this game and nothing is typed (IsAwaitingSoftRejoin, IsInTransfer)
    /// </summary>
    private int _softReturnZone;

    /// <summary>
    ///     Once told by the host (ZoneSoftRejoin, Travel), how long its map may take to be loaded here: the map
    ///     comes in the same frame, the placement one round trip later (after a save, on a host that saves by
    ///     itself there). Shorter than what is kept for an incoming map (ElinDeltaManager.MaxHoldSeconds):
    ///     nothing the host says of its map is ever applied to the one we are leaving
    /// </summary>
    private const float SoftMapWaitSeconds = 8f;

    /// <summary>
    ///     Slice 2: we walk from the map we hold alone into the map the host stands on, and nothing else is going
    ///     on. Not out of the world map nor the zone of a quest (no map of ours to hand back), not into them, and
    ///     not with something in our hands the game drops on the way out (Chara.TryDropCarryOnly: it would lie on
    ///     a map that is no longer ours)
    /// </summary>
    private bool CanReturnSoftly(int hostZoneUid)
    {
        return CanRejoinSoftly() && hostZoneUid != Session.AwayZone!.uid &&
               game.spatials.Find(hostZoneUid) is not ({ IsRegion: true } or { IsInstance: true }) &&
               pc.held is not { trait.CanOnlyCarry: true } && !pc.things.Any(t => t.trait.CanOnlyCarry);
    }

    /// <summary>
    ///     We hold that map alone, stand on it alive, and nothing else is going on
    /// </summary>
    private bool CanRejoinSoftly()
    {
        return Session.Rules.SoftRecall && core.IsGameStarted && Session.IsZoneAuthority && Session.ZoneSession is null &&
               Session.AwayZone is { IsRegion: false, IsInstance: false } away && _zone == away && _map is not null &&
               pc is { isDead: false } && _pendingTravel is null && _pendingGrant is null && !_rejoining &&
               _handoffDeadline <= 0 && !HasBusyWindow();
    }

    /// <summary>
    ///     A dialogue or a trade holds the character it talks to: the switch replaces our stale copies of the
    ///     world's characters by the host's (AdoptHostCharas), the window would keep the old one, and what is sold
    ///     or given through it would be gone or exist twice. The world copy closes everything: that path then
    /// </summary>
    private static bool HasBusyWindow()
    {
        return LayerDrama.IsActive() || ui.GetLayer<LayerDragGrid>() is not null || LayerInventory.listInv.Any(inv =>
            inv.invs.Count > 0 && inv.invs[0].owner is { } owner && owner.Container != pc && owner.Container.GetRootCard() != pc);
    }

    private void UpdateSoftRejoinWait()
    {
        if (_worldAskDeadline > 0 && Time.realtimeSinceStartup >= _worldAskDeadline) {
            _worldAskDeadline = 0;
            EmpLog.Warning("No world {Seconds:F0}s after asking for it, joining the game again", WorldAskWaitSeconds);
            if (Host is not null) {
                Socket.Disconnect(Host, EmpDisconnectInfo.RemoteClosed);
            }

            return;
        }

        if (_softRejoinDeadline <= 0 || Time.realtimeSinceStartup < _softRejoinDeadline) {
            return;
        }

        // the number the host waits under: its own map when we walk into it, the map we stay on otherwise
        AskWorldCopy(_softReturnZone != 0 ? _softReturnZone : Session.AwayZone?.uid ?? -1,
            Session.IsAway ? $"no answer after {SoftRejoinWaitSeconds:F0}s" : $"no map after {SoftMapWaitSeconds:F0}s");
    }

    /// <summary>
    ///     Never a question to the player: the host sends the world and its map, which replace this game whatever
    ///     state it is in (OnSaveDataProbe, as a player coming back without the rule)
    /// </summary>
    private void AskWorldCopy(int zoneUid, string reason)
    {
        EmpLog.Warning("Soft rejoin of {ZoneUid} given up ({Reason}), asking the host for the world",
            zoneUid, reason);

        _softRejoinDeadline = 0;
        _softPlaced = 0;
        _softReturnZone = 0;
        _worldAskDeadline = Time.realtimeSinceStartup + WorldAskWaitSeconds;

        // away again if the switch was made: nothing about the host map is taken until its world is here
        if (!Session.IsAway && _zone is { } here) {
            Session.AwayZone = here;
        }

        _rejoining = true;
        StopWorldStateUpdate();
        Delta.ClearOut();
        Delta.ClearIn();

        // link gone meanwhile: the lost link is handled where every lost link is
        Host?.Send(new ZoneSoftRejoinFailed {
            ZoneUid = zoneUid,
            Reason = reason,
        });
    }

    /// <summary>
    ///     Net event: the host stands on the map we handed back and runs it from now on
    /// </summary>
    private void OnZoneSoftRejoin(ZoneSoftRejoin rejoin)
    {
        if (_softRejoinDeadline <= 0) {
            // given up meanwhile: the host was told and sends the world
            EmpLog.Debug("Soft rejoin of {ZoneUid} arrives after we gave it up", rejoin.ZoneUid);
            return;
        }

        // the answer to what we asked: about the map we stay on, or (Travel) about the one we walk into
        var expected = rejoin.Travel ? _softReturnZone : _softReturnZone == 0 ? Session.AwayZone?.uid ?? -1 : -1;
        if (!_rejoining || !core.IsGameStarted || Session.AwayZone is not { } zone || rejoin.ZoneUid != expected ||
            _zone != zone || _map is null || pc.isDead) {
            AskWorldCopy(rejoin.ZoneUid, "not standing on that map as we left it");
            return;
        }

        try {
            if (ApplySoftRejoin(zone, rejoin) is { } refused) {
                AskWorldCopy(rejoin.ZoneUid, refused);
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Soft rejoin of {ZoneFullName} could not be applied", zone.ZoneFullName);
            AskWorldCopy(rejoin.ZoneUid, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <returns>why the copy of the world is needed after all, null when we are a client of the host again</returns>
    private string? ApplySoftRejoin(Zone zone, ZoneSoftRejoin rejoin)
    {
        var companions = CompanionHelper.TravellingWith(pc);
        var own = pc.things.Flatten().Concat(companions.SelectMany(c => c.things.Flatten())).ToList();

        // the cards that waited for a number of the host take the one it gave their copy there. The only step that
        // can make a thing exist twice (two numbers for one card): every one of them is found before any is touched
        var renumbered = new List<(Thing Thing, int Uid)>();
        foreach (var (pending, real) in rejoin.Rebinds ?? []) {
            if (own.Find(t => t.uid == pending) is not { } thing) {
                return $"card {pending} the host renumbered {real} is not ours";
            }

            if (own.Exists(t => t.uid == real)) {
                return $"number {real} given by the host is taken here";
            }

            renumbered.Add((thing, real));
        }

        // (ability tokens are each game's own, the host drops them from its copy)
        if (own.Find(t => PendingUid.IsPending(t.uid) && t.trait is not TraitAbility &&
                          rejoin.Rebinds?.ContainsKey(t.uid) is not true) is { } unnumbered) {
            return $"card {unnumbered.uid} waits for a number the host did not give";
        }

        foreach (var (thing, uid) in renumbered) {
            thing.uid = uid;
        }

        // what we carry, as the host holds it since our release: nothing moved here meanwhile (IsAwaitingSoftRejoin)
        var bag = NetDesync.Bag(pc);
        if (bag != rejoin.BagMix) {
            return $"bag differs (here {bag:X8}, host {rejoin.BagMix:X8})";
        }

        // slice 2: we walk into the host's map, which follows this message; ours stays behind, nothing to compare
        var travel = rejoin.Travel;
        var ours = ZoneLeaseState.Sums(_map);
        var sameMap = travel || (rejoin.MapSums is { } theirs && ZoneLeaseState.SameFloor(ours, theirs));
        if (!travel && sameMap && !ours.SequenceEqual(rejoin.MapSums!)) {
            // as when a map changes hands (AdoptHostCopy): told, not a reason to load the map under the player
            EmpLog.Information("Soft rejoin of {ZoneFullName}: the content of a container differs, our copy is kept (here {Local} | host {Host})",
                zone.ZoneFullName, ZoneLeaseState.TellSums(ours), ZoneLeaseState.TellSums(rejoin.MapSums!));
        }

        // still away from here to the switch: nothing of what follows is told to the host as an action of ours
        FillWorldBoxes(rejoin.Boxes);

        if (rejoin.GameDate is { } date) {
            WorldDateAdvanceDelta.SetClientDate(date);
        }

        var arrived = AdoptHostCharas(rejoin.Charas);
        if (travel) {
            ForgetAbsentCharas(rejoin.ZoneUid, arrived);
        }

        // ponytail: our running task is stopped as by hand, nothing used up (lost today too, with the whole scene).
        // The host knows nothing of it and a client's progress is held by the game that keeps the map
        // (PendingOnHost). Kept when seen missing: start it again through the client's own path once switched
        if (pc.ai is { IsRunning: true } task && !pc.HasNoGoal) {
            EmpLog.Information("Soft rejoin of {ZoneFullName}: {ActType} is stopped", zone.ZoneFullName, task.GetType().Name);
            pc.Say("cancel_act_pc", pc);
            task.Stub_Cancel();
        }

        // the switch, mirror of EnterAway: from now on the host simulates this map (NetSession.Connection is the
        // host link again: only our own character ticks here, see CharaTickEvent) and its deltas are taken
        Session.IsGuest = false;
        Session.AwayZone = null;
        // walking in: the map that follows says which zone it is (OnZoneDataResponse), known here or not
        Session.CurrentZone = travel ? game.spatials.Find(rejoin.ZoneUid) ?? Session.CurrentZone : zone;
        _pendingTravel = null;
        _pendingGrant = null;
        _rejoining = false;
        _handoffDeadline = 0;
        _awaitingActivation = 0;
        // walking in: still nothing moves nor is typed here until the host's map is loaded (EnterBySoftReturn),
        // and should it not come the world is asked for
        _softRejoinDeadline = travel ? Time.realtimeSinceStartup + SoftMapWaitSeconds : 0;
        _softPlaced = travel ? 0 : zone.uid;
        ReportReturn(0);

        // as the host's uid counter: a client takes it from every snapshot (WorldStateSnapshot)
        game.cards.uidNext = rejoin.UidNext;

        // nothing of this game was a host card while we were alone: every card of the map, of our bag and of the
        // boxes is one now, under the number both games have. Walking in: the map we leave is no longer ours, the
        // one that follows is cached when it is loaded, our bag with it (ZoneActivateEvent)
        CardCache.Reset();
        if (!travel) {
            CardCache.CacheCurrentZone();
        }

        foreach (var chara in arrived) {
            CardCache.Add(chara);
            CardCache.CacheContainer(chara.things);
        }

        // ability tokens are no host cards: ours are told to the host as a client tells them
        foreach (var token in pc.things.Where(t => t.trait is TraitAbility).ToList()) {
            CardCache.Remove(token.uid);
        }

        CardAddThingEvent.AbilityLayoutDirty = true;

        Delta.ClearOut();
        Delta.ClearIn();
        // not compared during the switch: the first numbers of the host land with the list of players
        NetDesync.HoldOff();
        StartWorldStateUpdate();

        if (travel) {
            // from this message on the host's deltas are about a map that is not loaded here yet: kept, and
            // replayed once we stand on it (Delta.MapPlaced in OnZoneActivateResponse). What it said before was
            // thrown away while we were away (ApplyChatWhileAway) and is in the copy of the map that follows
            Delta.HoldForIncomingMap();

            EmpLog.Information("Soft return from {ZoneFullName} to zone {ZoneUid}: client of the host again, no world copy, its map follows ({Renumbered} card(s) renumbered, {Charas} character(s) taken)",
                zone.ZoneFullName, rejoin.ZoneUid, renumbered.Count, arrived.Count);
            return null;
        }

        EmpLog.Information("Soft rejoin of {ZoneFullName}: client of the host again in place, no world copy ({Renumbered} card(s) renumbered, {Charas} character(s) taken, map {Sums}, same as the host's {SameMap})",
            zone.ZoneFullName, renumbered.Count, arrived.Count, ZoneLeaseState.TellSums(ours), sameMap);

        if (!sameMap) {
            // the map alone, loaded under our feet as NetDesync repairs one: still no copy of the world
            EmpLog.Warning("Soft rejoin of {ZoneFullName}: our copy of the map differs, asking for the map alone (here {Local} | host {Host})",
                zone.ZoneFullName, ZoneLeaseState.TellSums(ours), ZoneLeaseState.TellSums(rejoin.MapSums ?? []));

            // before the request: the host notes where we stand before it answers the map
            Host.Send(new WorldStateDeltaList {
                DeltaList = [
                    new DesyncReportDelta {
                        ZoneFullName = zone.ZoneFullName,
                        Detail = "soft rejoin, map numbers differ",
                        Resync = true,
                    },
                ],
            });
            MarkResync();
            RequestZoneState(MapDataRequest.CurrentRemoteZone);
        }

        return null;
    }

    /// <summary>
    ///     The host placed our character on the map we stayed on (ElinNetHost.OnZoneDataReceivedResponse, with the
    ///     tile our release told): our own tile, unless it could not have it there
    /// </summary>
    private void ConfirmSoftPlacement(ZoneActivateResponse response)
    {
        EmpLog.Debug("Soft rejoin of {ZoneUid}: the host holds us at {@Pos}", response.ZoneUid, response.Pos);

        if (pc.isDead || _zone?.uid != response.ZoneUid || !response.Pos.IsInActiveMapBounds ||
            (pc.pos.x == response.Pos.X && pc.pos.z == response.Pos.Z)) {
            return;
        }

        pc.Place(response.Pos);
        pc.SetDir(pc.dir);
    }

    /// <summary>
    ///     Slice 2: the host said where we stand on its map, the first one loaded since we are its client again
    ///     without a copy of the world. Loaded as a client following the host loads one (EnterHostMap), with what
    ///     a game that just left a map of its own needs around it
    /// </summary>
    /// <returns>false when the map could not be loaded: the world is asked for</returns>
    private bool EnterBySoftReturn(Zone hostZone)
    {
        var asked = _softReturnZone;
        _softReturnZone = 0;
        _softRejoinDeadline = 0;

        try {
            var left = _zone;
            var carried = pc.things.Flatten().Select(t => t.uid).ToHashSet();

            // our companions come along, as when we travel alone (CharaMoveZoneEvent.OnTravelWithCompanions: the
            // game only drags the party of its leader). The host puts each on its tile afterwards
            foreach (var companion in CompanionHelper.CompanionsOf(pc)) {
                if (!companion.isDead && companion.parent is Zone && companion.currentZone != hostZone) {
                    companion.MoveZone(hostZone);
                }
            }

            // what we summoned stays on the map we handed back, where the host has it: the game would carry it
            // over (Chara.MoveZone, player.listCarryoverMap) as a character the host knows nothing of here
            if (left is not null && left != hostZone && _map is { } leaving) {
                foreach (var minion in leaving.charas
                             .Where(c => c.c_uidMaster != 0 && c.c_minionType == MinionType.Default && c.FindMaster() == pc)
                             .ToList()) {
                    left.RemoveCard(minion);
                }
            }

            EnterHostMap(hostZone);

            // the game puts the artifacts lying on the map we leave in our bag on the way out (Zone.Deactivate):
            // they are on the copy we handed back, see AdoptHostCopy
            foreach (var thing in pc.things.Flatten().Where(t => !carried.Contains(t.uid)).ToList()) {
                EmpLog.Warning("Soft return: {CardId} {Uid} taken on the way out stays on the map we handed back", thing.id, thing.uid);
                CardCache.Remove(thing.uid);
                thing.parentCard?.RemoveCard(thing);
            }

            // leaving a map of our own is no action of ours on the host's: nothing of it is told
            Delta.ClearOut();

            // ability tokens are no host cards (the load cached our whole bag): told as a client tells them
            foreach (var token in pc.things.Where(t => t.trait is TraitAbility).ToList()) {
                CardCache.Remove(token.uid);
            }

            CardAddThingEvent.AbilityLayoutDirty = true;

            EmpLog.Information("Soft return: map {ZoneFullName} loaded, no world copy", hostZone.ZoneFullName);
            return true;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Soft return: map {ZoneFullName} could not be loaded", hostZone.ZoneFullName);
            AskWorldCopy(asked, $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Slice 2: the map we walk into is one we did not hold, and our world is as old as its last copy. The
    ///     host listed the characters of the world standing on its map (AdoptHostCharas): whoever else our world
    ///     still puts there is elsewhere by now, and must not be placed by the load
    ///     (Zone.AddGlobalCharasOnActivate). The other players that are not there, and their companions, are
    ///     forgotten as a client forgets them when they leave (CharaRemoveFromGameDelta, not taken while away):
    ///     they come whole when they arrive (CardGenDelta), instead of our old copy being taken for them
    /// </summary>
    private static void ForgetAbsentCharas(int hostZoneUid, List<Chara> arrived)
    {
        var there = arrived.Select(c => c.uid).ToHashSet();

        foreach (var chara in game.cards.globalCharas.Values.ToList()) {
            if (chara == pc || there.Contains(chara.uid) || chara.CompanionOwnerUid == pc.uid || chara.IsCompanionOf(pc)) {
                continue;
            }

            var ofAnotherPlayer = chara.GetBool("remote_chara") || chara.CompanionOwnerUid != 0;
            if (!ofAnotherPlayer && chara.currentZone?.uid != hostZoneUid) {
                continue;
            }

            if (_map.charas.Contains(chara)) {
                _zone.RemoveCard(chara);
            } else {
                if (chara.parent is Zone) {
                    chara.parent = null;
                }

                chara.currentZone = null;
            }

            if (!ofAnotherPlayer) {
                continue;
            }

            if (pc.party is { } party && party.members.Contains(chara)) {
                party.Stub_RemoveMember(chara);
            }

            game.cards.globalCharas.Remove(chara);
        }
    }

    /// <summary>
    ///     The boxes of the world were emptied when we went away (EmptyWorldContainers) and only showed pictures
    ///     since (ShippingHelper.MirrorKey): they hold what the host's hold again, under the host's numbers
    /// </summary>
    private static void FillWorldBoxes(List<LZ4Bytes>? boxes)
    {
        if (boxes is null) {
            return;
        }

        ShippingHelper.FillingMirror = true;
        try {
            for (var box = 0; box < boxes.Count; box++) {
                if (ShippingHelper.WorldBox(box) is not { } container) {
                    continue;
                }

                EmptyWorldContainer(container);
                foreach (var thing in boxes[box].Decompress<List<Thing>>()) {
                    container.AddThing(thing, false);
                }
            }
        } finally {
            ShippingHelper.FillingMirror = false;
        }

        LayerInventory.SetDirtyAll();
    }

    /// <summary>
    ///     The characters of the world standing on the host's map (the host, other players, companions) replace
    ///     the copies of our last world, as ElinNetHost.ReplaceCompanions does there. Off map here: the positions
    ///     the host sends put them on it (CharaStateSnapshot.ApplyReconciliation)
    /// </summary>
    private static List<Chara> AdoptHostCharas(List<LZ4Bytes>? charas)
    {
        var adopted = new List<Chara>();
        var party = pc.party;
        // ours by our own world too: a host's copy of our mount that it did not count as ours replaced it here, our
        // character went on riding a copy no longer of the world and the mount left our party bar (issue #43)
        var ours = CompanionHelper.CompanionsOf(pc).Select(c => c.uid).ToHashSet();

        foreach (var data in charas ?? []) {
            var fresh = data.Decompress<Chara>();
            // ours are the ones we play and just handed over
            if (fresh.uid == pc.uid || fresh.CompanionOwnerUid == pc.uid || ours.Contains(fresh.uid)) {
                continue;
            }

            if (game.cards.globalCharas.Find(fresh.uid) is { } old) {
                if (_map.charas.Contains(old)) {
                    _zone.RemoveCard(old);
                }

                game.cards.globalCharas.Remove(old);
                CompanionHelper.UnlinkWork(old);

                // branches hold object references
                if (old.homeBranch is { } branch && branch.members.IndexOf(old) is var index and >= 0) {
                    branch.members[index] = fresh;
                }
            }

            fresh.currentZone = null;
            game.cards.globalCharas.Add(fresh);

            // the copy carries its own copy of the party, rebuild members from the uids
            if (party is not null) {
                var member = !fresh.isDead && fresh.party?.uidMembers.Contains(fresh.uid) is true;
                fresh.party = member ? party : null;
                if (member && !party.uidMembers.Contains(fresh.uid)) {
                    party.uidMembers.Add(fresh.uid);
                } else if (!member) {
                    party.uidMembers.Remove(fresh.uid);
                }

                if (party.uidLeader == fresh.uid) {
                    party.leader = fresh;
                }

                party.SetMembers();
            }

            adopted.Add(fresh);
        }

        // (each came written apart: a rider and its mount are tied to each other's card of this world again)
        foreach (var chara in adopted) {
            CompanionHelper.RebindRide(chara);
        }

        return adopted;
    }
}
