using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Patches;

namespace ElinTogether.Net;

/// <summary>
///     Companions of a player travel with it: off this map while it is away, back next to it when it returns
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     The player leaves this map (travelling alone, disconnecting): its companions go with it and stay
    ///     in the party, off map, until it comes back, see BringCompanions
    /// </summary>
    private void TakeCompanionsAlong(Chara player)
    {
        if (EClass.game?.player?.chara is null) {
            return;
        }

        foreach (var companion in CompanionHelper.CompanionsOf(player)) {
            if (companion.isDead || companion.currentZone is null) {
                continue;
            }

            companion.SetNoGoal();
            if (companion.parent is Zone zone) {
                zone.RemoveCard(companion);
            }

            companion.currentZone = null;

            Delta.AddRemote(new CharaRemoveFromGameDelta {
                Owner = companion,
            });

            EmpLog.Debug("Companion {Uid} leaves with player {OwnerUid}",
                companion.uid, player.uid);
        }
    }

    /// <summary>
    ///     The player leaves this map on its own: what it summoned goes with it, as the game carries the minions of
    ///     a player leaving a map over to the next one (Chara.MoveZone, player.listCarryoverMap). Its own game
    ///     does that in its copy; left here too, the same minion stood on two maps at once (audit 2026-10-23)
    /// </summary>
    private void TakeMinionsAlong(Chara player)
    {
        if (!SummonsFollowMaster.Enabled || _map is null || _zone is null) {
            return;
        }

        foreach (var minion in _map.charas.Where(c => SummonsFollowMaster.IsCarriedMinion(c, player)).ToList()) {
            minion.SetNoGoal();
            _zone.RemoveCard(minion);
            CardCache.Remove(minion.uid);

            // its master keeps it, see CharaRemoveFromGameDelta
            Delta.AddRemote(new CharaRemoveFromGameDelta {
                Owner = minion,
            });

            EmpLog.Debug("Minion {Uid} leaves with player {OwnerUid}",
                minion.uid, player.uid);
        }
    }

    /// <summary>
    ///     The player stands on this map again, its companions join it
    /// </summary>
    /// <param name="keepSpot">back on the map they never really left: each where it stood, not around the player</param>
    private void BringCompanions(Chara player, bool keepSpot = false)
    {
        foreach (var companion in CompanionHelper.CompanionsOf(player)) {
            if (companion.isDead || companion.currentZone is not null) {
                continue;
            }

            // its mount, and what rides it, stand on the player's own tile, as the game keeps them
            var carried = companion == player.ride || companion == player.parasite;
            var near = keepSpot && companion.pos is { IsValid: true, IsInBounds: true } ? companion.pos : player.pos;
            var pos = carried ? player.pos.Copy() : near.GetNearestPoint(allowChara: false, allowInstalled: false) ?? near.Copy();

            // the game may already have laid it on the player's tile (Chara.SyncRide, when the player was put on the
            // map): laid a second time beside it, it stayed in the cell of the first for good
            if (companion.pos is { IsValid: true, IsInBounds: true }) {
                _map._RemoveCard(companion);
            }

            Delta.AddRemote(CardGenDelta.Create(companion));
            _zone.AddCard(companion, pos);

            CardCache.Add(companion);
            CardCache.CacheContainer(companion.things);

            // replicas dropped it from their party when it left
            Delta.AddRemote(new CharaMakeAllyDelta {
                Owner = companion,
                ShowMsg = false,
                TemporaryAllyName = companion.c_altName,
                OwnerUid = companion.CompanionOwnerUid,
            });

            EmpLog.Debug("Companion {Uid} back with player {OwnerUid} at {@Pos}",
                companion.uid, player.uid, pos);
        }
    }

    /// <summary>
    ///     Swap the host copies of the companions for the ones simulated by their travelling owner
    /// </summary>
    /// <param name="rebound">filled with the numbers given to their cards that waited for one: old -> new</param>
    private void ReplaceCompanions(List<LZ4Bytes>? companions, int ownerUid, Dictionary<int, int>? rebound = null)
    {
        if (ownerUid == 0) {
            return;
        }

        if (companions is null) {
            RebindRide(ownerUid);
            return;
        }

        var party = pc.party;
        var kept = new HashSet<int>();

        foreach (var data in companions) {
            var uploaded = data.Decompress<Chara>();
            var old = game.cards.globalCharas.Find(uploaded.uid);

            // what our own game says that player rides or carries is its own, whatever its tag says here: a mount is
            // its rider's companion, as the game puts it in the party of whoever rides it (issue #43: a mount and a
            // parasite tagged as nobody's here stayed behind, frozen on the tile their rider left from, and came back
            // to the rider's game as the host's, out of its party)
            var carried = old is { IsPlayer: false, isDead: false } && old.host?.uid == ownerUid;

            // only that player's own companions, or a new one met on its map: never a player, nor a character of
            // someone else or of the world (security audit 2026-10-27)
            // (a resident of nobody, or the world's: recruited on the way by a friend; refused only on a public server)
            if (SavedRemoteCharas.ContainsValue(uploaded.uid) || uploaded.uid == pc.uid ||
                (old is not null && !carried && old.CompanionOwnerUid != ownerUid &&
                 (old.CompanionOwnerUid != 0 || (ElinDelta.TrustEnforced && (old.IsPCFaction || old.IsGlobal))))) {
                EmpLog.Warning("Companion {Uid} uploaded for player {OwnerUid} is not its own: refused", uploaded.uid, ownerUid);
                continue;
            }

            kept.Add(uploaded.uid);

            if (old is not null && old.parent is Zone && old.currentZone == _zone) {
                // never left this map with its owner, the host copy is the live one
                EmpLog.Warning("Companion {Uid} of player {OwnerUid} is on the host map, keeping host copy",
                    uploaded.uid, ownerUid);
                if (carried) {
                    ClaimCarried(old, ownerUid, party);
                }

                continue;
            }

            ForgetCachedCard(uploaded);
            if (old is not null) {
                // an investment in its shop only ever goes up: a copy that missed the host's keeps it (issue #41)
                if (old.c_invest > uploaded.c_invest) {
                    uploaded.c_invest = old.c_invest;
                }

                ForgetCachedCard(old);
                game.cards.globalCharas.Remove(old);
                CompanionHelper.UnlinkWork(old);

                // branches hold object references
                if (old.homeBranch is { } branch && branch.members.IndexOf(old) is var index and >= 0) {
                    branch.members[index] = uploaded;
                }
            }

            uploaded.SetInt(CompanionHelper.OwnerKey, ownerUid);
            // off map until the owner comes back, see BringCompanions
            uploaded.currentZone = null;
            game.cards.globalCharas.Add(uploaded);
            JoinItsBase(uploaded);

            // the uploaded copy carries its own copy of the party, rebuild members from the uids
            // died on the way: out of the party, dead, until someone revives it
            var member = !uploaded.isDead && uploaded.party?.uidMembers.Contains(uploaded.uid) is true;
            uploaded.party = member ? party : null;
            if (member && !party.uidMembers.Contains(uploaded.uid)) {
                party.uidMembers.Add(uploaded.uid);
            } else if (!member && party.uidMembers.Remove(uploaded.uid)) {
                EmpLog.Information("Companion {Uid} of player {OwnerUid} died while travelling",
                    uploaded.uid, ownerUid);
            }

            party.SetMembers();

            foreach (var thing in uploaded.things.Flatten().ToList()) {
                if (PendingUid.IsPending(thing.uid)) {
                    var pending = thing.uid;
                    game.cards.AssignUID(thing);
                    rebound?[pending] = thing.uid;
                }
            }

            EmpLog.Debug("Replaced companion {Uid} of player {OwnerUid}",
                uploaded.uid, ownerUid);
        }

        // let go while travelling (only those travelling with it, off map here)
        foreach (var gone in party.members.ToList()) {
            if (gone is null || gone.CompanionOwnerUid != ownerUid || kept.Contains(gone.uid) ||
                gone.currentZone is not null || gone.GetBool("remote_chara")) {
                continue;
            }

            EmpLog.Information("Companion {Uid} left player {OwnerUid} while travelling",
                gone.uid, ownerUid);

            party.RemoveMember(gone);
            gone.SetInt(CompanionHelper.OwnerKey, 0);
            if (gone.homeZone is { } home) {
                gone.MoveZone(home);
            }
        }

        RebindRide(ownerUid);
    }

    /// <summary>
    ///     The host's copy of what a player carries, left on this map: that player's companion again, in the party,
    ///     told to everyone (its tag is what the party bar of each game and the next trip go by)
    /// </summary>
    private void ClaimCarried(Chara carried, int ownerUid, Party party)
    {
        if (carried.CompanionOwnerUid == ownerUid && carried.party == party && party.uidMembers.Contains(carried.uid)) {
            return;
        }

        EmpLog.Information("Companion {Uid} carried by player {OwnerUid} was tagged {Tag} here: that player's again",
            carried.uid, ownerUid, carried.CompanionOwnerUid);
        carried.SetInt(CompanionHelper.OwnerKey, ownerUid);
        carried.party = party;
        if (!party.uidMembers.Contains(carried.uid)) {
            party.uidMembers.Add(carried.uid);
        }

        party.SetMembers();
        Delta.AddRemote(new CharaMakeAllyDelta {
            Owner = carried,
            ShowMsg = false,
            TemporaryAllyName = carried.c_altName,
            OwnerUid = ownerUid,
            JoinOnly = true,
        });
    }

    // recruited while its player was alone: a member of the base in that player's copy of the world only
    private static void JoinItsBase(Chara chara)
    {
        if (chara.homeBranch is { } home && !home.members.Contains(chara)) {
            home.members.Add(chara);
        }
    }

    /// <summary>
    ///     The characters of the base a player made or let go while it was alone, and that did not come back with
    ///     it (ZoneLeaseRelease.Residents): they exist here from now on, members of their base, at their base. Also
    ///     every resident of the base it held and may manage (<paramref name="managedZoneUid" />), as its game left
    ///     them: equipped, fed, trained, away on an expedition (lease audit 2026-10-29, #1)
    /// </summary>
    private void ReplaceResidents(List<LZ4Bytes>? residents, int ownerUid, int managedZoneUid = -1)
    {
        if (residents is null || ownerUid == 0) {
            return;
        }

        var party = pc.party;
        foreach (var data in residents) {
            var uploaded = data.Decompress<Chara>();
            var old = game.cards.globalCharas.Find(uploaded.uid);
            if (uploaded.GetBool("remote_chara") || (old is { parent: Zone } && old.currentZone == _zone)) {
                // (never a player's character; one that stands on this map is the host's to tell about)
                continue;
            }

            // only what that player could let go: a character nobody knew, or one that travelled with it
            // or a resident of the base it held, staying a resident of that base
            var ofManaged = managedZoneUid != -1 && old?.homeZone?.uid == managedZoneUid && uploaded.homeZone?.uid == managedZoneUid &&
                            old!.CompanionOwnerUid == 0;
            if (old is not null && old.CompanionOwnerUid != ownerUid && !ofManaged) {
                EmpLog.Warning("Resident {Uid} sent by player {OwnerUid} is not its own, left as it is", uploaded.uid, ownerUid);
                continue;
            }

            ForgetCachedCard(uploaded);
            if (old is not null) {
                // an investment in its shop only ever goes up: a copy that missed the host's keeps it (issue #41)
                if (old.c_invest > uploaded.c_invest) {
                    uploaded.c_invest = old.c_invest;
                }

                ForgetCachedCard(old);
                game.cards.globalCharas.Remove(old);
                CompanionHelper.UnlinkWork(old);
                if (old.homeBranch is { } branch && branch.members.IndexOf(old) is var index and >= 0) {
                    branch.members[index] = uploaded;
                    // what it wears counts for the faction (SetOwner, AddMemeber)
                    pc.faction.charaElements.OnRemoveMember(old);
                    pc.faction.charaElements.OnAddMemeber(uploaded);
                }
            }

            // where its game left it: away on an expedition (Expedition.Start), or at home where it stood
            var at = uploaded.currentZone;

            uploaded.SetInt(CompanionHelper.OwnerKey, 0);
            uploaded.party = null;
            if (party.uidMembers.Remove(uploaded.uid)) {
                party.SetMembers();
            }

            uploaded.currentZone = null;
            game.cards.globalCharas.Add(uploaded);
            JoinItsBase(uploaded);

            foreach (var thing in uploaded.things.Flatten().ToList()) {
                if (PendingUid.IsPending(thing.uid)) {
                    game.cards.AssignUID(thing);
                }
            }

            // (dead: it waits at its base as the game keeps the dead of a base, nothing to place)
            if (ofManaged && at is { id: "somewhere" }) {
                uploaded.currentZone = at;
            } else if (!uploaded.isDead && uploaded.homeZone is { } home) {
                uploaded.MoveZone(home);
            }

            // its work counts for its base from this copy, as the old one's did (none in a party: RefreshWorkElements)
            if (uploaded.homeBranch is { } itsBase && itsBase.members.Contains(uploaded)) {
                uploaded.RefreshWorkElements(itsBase.elements);
            }

            EmpLog.Information("Resident {Uid} made or let go by player {OwnerUid} while alone: now at its base {ZoneUid}",
                uploaded.uid, ownerUid, uploaded.homeZone?.uid ?? 0);
        }
    }

    /// <summary>
    ///     The characters of the base a player butchered or banished while it was alone (ZoneLeaseRelease.GoneResidents):
    ///     the host's copy goes too, it was the only one left and came back ("dodos respawned after getting butchered")
    /// </summary>
    private void DropGoneResidents(List<int>? gone, int ownerUid)
    {
        if (gone is null || ownerUid == 0) {
            return;
        }

        foreach (var uid in gone.Take(256)) {
            // never a player's character, never one standing on the map the host plays, never a stranger's companion
            // (a base member without a base is not a resident), never one with a story of its own
            if (game.cards.globalCharas.Find(uid) is not { isDestroyed: false } old || old.IsPlayer || old.GetBool("remote_chara") ||
                old.homeBranch is not { } branch || old.trait.IsUnique || old.IsUnique ||
                (old.CompanionOwnerUid != 0 && old.CompanionOwnerUid != ownerUid) ||
                (old.parent is Zone && old.currentZone == _zone)) {
                continue;
            }

            ForgetCachedCard(old);
            if (old.IsPCParty) {
                pc.party.RemoveMember(old);
            }

            branch.RemoveMemeber(old);
            game.cards.globalCharas.Remove(old);
            old.Destroy();

            EmpLog.Information("Resident {Uid} went while player {OwnerUid} was alone: dropped here too", uid, ownerUid);
        }
    }

    private static void RebindRide(int ownerUid)
    {
        if (game.cards.globalCharas.Find(ownerUid) is { } player) {
            CompanionHelper.RebindRide(player);
        }
    }

    // a player whose link dropped -> the rides between it and another player it was part of (rider, carried, parasite)
    private static readonly Dictionary<ulong, (List<(int Rider, int Carried, bool Parasite)> Ties, float At)> RideTiesAtLinkLoss = [];

    /// <summary>
    ///     A ride between two players breaks when one of them drops off the map (the game and the mod untie it), and
    ///     nothing tied it again on its automatic return: the host's game said nobody rode, the returning player's
    ///     game still did (ride2 cut_home, 2026-10-26). The ties with another player are kept for that return
    /// </summary>
    private static void KeepRideTies(ulong user, Chara player)
    {
        var ties = CompanionHelper.PlayerRideTies(player);
        if (ties.Count > 0) {
            RideTiesAtLinkLoss[user] = (ties, UnityEngine.Time.realtimeSinceStartup);
        } else {
            RideTiesAtLinkLoss.Remove(user);
        }
    }

    private static void RestoreRideTies(ulong user)
    {
        if (RideTiesAtLinkLoss.Remove(user, out var kept) &&
            UnityEngine.Time.realtimeSinceStartup - kept.At <= LinkLostGraceSeconds) {
            EmpLog.Debug("Ride ties of {User} to restore: {Ties}", user, string.Join(" ", kept.Ties));
            CompanionHelper.TieAgain(kept.Ties);
        }
    }

    /// <summary>
    ///     A world loaded with a ride between the host and another player (saved while one carried the other): that
    ///     player is not here yet and the tie is dropped as it is away; its return ties it again, as after a dropped
    ///     link (ride2 reload, 2026-10-26: the host's game said nobody rode, the guest's world copy still did)
    /// </summary>
    // the rides between players read from the save, kept before RemoveLeftOverCharas takes the players off the
    // map (which unties them, RemoteRideHostPatch.OnRemoveCard): the host lost its seat at each reload (ride2 h-on-g)
    internal static readonly List<(int Rider, int Carried, bool Parasite)> LoadedRideTies = [];

    [ElinPreLoad]
    private static void ForgetLoadedRideTies(GameIOContext context)
    {
        LoadedRideTies.Clear();
    }

    private static void KeepSavedRideTies()
    {
        // (read from both ends: a save may keep the rider's side only)
        List<(int Rider, int Carried, bool Parasite)> mine = pc is { } host ? CompanionHelper.PlayerRideTies(host) : [];
        foreach (var (user, uid) in SavedRemoteCharas) {
            if (game.cards.globalCharas.Find(uid) is not { } player) {
                continue;
            }

            var ties = CompanionHelper.PlayerRideTies(player);
            ties.AddRange(mine.Concat(LoadedRideTies)
                .Where(t => (t.Rider == uid || t.Carried == uid) && !ties.Contains(t)).Distinct());
            if (ties.Count > 0) {
                RideTiesAtLinkLoss[user] = (ties, UnityEngine.Time.realtimeSinceStartup);
            }
        }

        EmpLog.Debug("Ride ties read from the save: {Loaded}, kept for {Players} player(s)",
            string.Join(" ", LoadedRideTies), RideTiesAtLinkLoss.Count);
        LoadedRideTies.Clear();
    }
}
