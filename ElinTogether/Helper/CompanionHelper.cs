using System.Collections.Generic;
using System.Linq;

namespace ElinTogether.Helper;

/// <summary>
///     Every player keeps its own companions inside the one party Elin knows, see MODLOG (étape 3) <br />
///     The owner is the chara uid of the player who recruited it, none means the party leader (the host)
/// </summary>
internal static class CompanionHelper
{
    internal const string OwnerKey = "emp_owner";

    /// <summary>
    ///     A copy from another game takes the place of <paramref name="old" /> in its base: what the old one's work and
    ///     hobbies gave the base (admin points, power, food... linked into the zone's elements by RefreshWorkElements)
    ///     goes with it. Left linked, every swap added the resident's work once more: admin points 20/78 and power on
    ///     a level 4 meadow with several players coming and going (issue #54)
    /// </summary>
    internal static void UnlinkWork(Chara old)
    {
        old.workElements?.SetParent();
        old.workElements = null;
    }

    extension(Chara chara)
    {
        /// <summary>
        ///     Chara uid of the player this companion follows, 0 for the party leader
        /// </summary>
        internal int CompanionOwnerUid => chara.GetInt(OwnerKey);

        internal bool IsCompanionOf(Chara player)
        {
            if (chara == player || chara.IsPC || chara.GetBool("remote_chara") || chara.party is not { } party ||
                !party.members.Contains(chara)) {
                return false;
            }

            var owner = chara.CompanionOwnerUid;
            return owner == player.uid || (owner == 0 && party.leader == player);
        }

        internal void SetCompanionOwner(Chara? player)
        {
            chara.SetInt(OwnerKey, player?.uid ?? 0);
        }

        /// <summary>
        ///     The player to follow when it stands in this zone, otherwise the party leader
        /// </summary>
        internal Chara? FindCompanionOwnerHere()
        {
            var uid = chara.CompanionOwnerUid;
            if (uid == 0 || chara.IsPC || chara.GetBool("remote_chara")) {
                return null;
            }

            return EClass.game.cards.globalCharas.Find(uid) is { IsAliveInCurrentZone: true } owner ? owner : null;
        }
    }

    /// <summary>
    ///     The player a party member belongs to: itself for a player, its owner for a companion
    /// </summary>
    internal static Chara? OwnerOf(Chara chara)
    {
        if (chara.IsPC || chara.GetBool("remote_chara")) {
            return chara;
        }

        if (chara.party is not { } party || !party.members.Contains(chara)) {
            return null;
        }

        // (an untagged one is the leader's, unless the leader is a server's stand-in: nobody's then, see ServerAvatar)
        return chara.CompanionOwnerUid is var uid and not 0 ? EClass.game.cards.globalCharas.Find(uid)
            : ServerAvatar.Is(party.leader) ? null : party.leader;
    }

    /// <summary>
    ///     Ally slots the player uses, as Party.Count counts them (big allies take more), without the player itself
    /// </summary>
    internal static int UsedAllySlots(Chara player)
    {
        var used = player.Evalue(1431);
        // (as CompanionsOf lists them, without building the list: MaxHP of every remote player's companion asks)
        if ((player.party ?? EClass.pc?.party) is { } party) {
            foreach (var companion in party.members) {
                if (companion is not null && companion.IsCompanionOf(player)) {
                    used += 1 + companion.Evalue(1431);
                }
            }
        }

        return used;
    }

    /// <summary>
    ///     What goes up with a player's checkpoints: its companions, and those that died on the way
    ///     (Elin takes a dead ally out of the party, it stays dead until revived)
    /// </summary>
    internal static List<Chara> TravellingWith(Chara player)
    {
        var list = CompanionsOf(player);
        foreach (var chara in EClass.game.cards.globalCharas.Values) {
            if (chara is { isDead: true, c_wasInPcParty: true } && chara.CompanionOwnerUid == player.uid &&
                !list.Contains(chara)) {
                list.Add(chara);
            }
        }

        return list;
    }

    /// <summary>
    ///     Companions of the player, wherever they are
    /// </summary>
    internal static List<Chara> CompanionsOf(Chara player)
    {
        var party = player.party ?? EClass.pc?.party;
        var list = new List<Chara>();
        if (party is null) {
            return list;
        }

        foreach (var c in party.members) {
            if (c is not null && c.IsCompanionOf(player)) {
                list.Add(c);
            }
        }

        return list;
    }

    /// <summary>
    ///     A player and its companions come one by one, each written apart: a rider carries its own copy of its
    ///     mount, and the mount its own copy of its rider. Left that way the rider rode a copy nobody else knew
    ///     (it followed it from tile to tile, on no list of the map), and the real mount stood where it was last
    ///     put, then kept the tile of another map: the world could not be loaded any more (2026-10-09). Each is
    ///     tied to the one of this world again. The same for what rides the player (parasite)
    /// </summary>
    internal static void RebindRide(Chara player)
    {
        var game = EClass.game;
        if (player.ride is { } ride && game.cards.globalCharas.Find(ride.uid) is { } mount && !ReferenceEquals(mount, ride)) {
            player.ride = mount;
        }

        if (player.parasite is { } parasite && game.cards.globalCharas.Find(parasite.uid) is { } rider &&
            !ReferenceEquals(rider, parasite)) {
            player.parasite = rider;
        }

        foreach (var carried in new[] { player.ride, player.parasite }) {
            if (carried is not null && !ReferenceEquals(carried.host, player)) {
                carried.host = player;
                // its ride look comes with a renderer made while carried, as mounting does (issue #4: an old copy
                // made while nobody rode it, tied here, made the rider's draw throw every frame)
                carried._CreateRenderer();
            }
        }

        // (a companion that still says it carries a copy of this player, which no longer rides it)
        foreach (var companion in CompanionHelper.CompanionsOf(player)) {
            if (companion.host is { } host && host.uid == player.uid && !ReferenceEquals(host, player)) {
                companion.host = companion == player.ride || companion == player.parasite ? player : null;
            }
        }

        EmpLog.Debug("Ride of player {Uid}: mount {Ride}, rider on it {Parasite}",
            player.uid, player.ride?.uid ?? 0, player.parasite?.uid ?? 0);
    }

    /// <summary>
    ///     The rides between two players a character is part of: (rider, carried, parasite) uids
    /// </summary>
    internal static List<(int Rider, int Carried, bool Parasite)> PlayerRideTies(Chara player)
    {
        var ties = new List<(int, int, bool)>();
        foreach (var (carried, parasite) in new[] { (player.ride, false), (player.parasite, true) }) {
            if (carried is not null && Patches.PlayerPeoplePatch.IsPlayerChara(carried)) {
                ties.Add((player.uid, carried.uid, parasite));
            }
        }

        if (player.host is { } rider && Patches.PlayerPeoplePatch.IsPlayerChara(rider)) {
            ties.Add((rider.uid, player.uid, rider.parasite?.uid == player.uid));
        }

        return ties;
    }

    /// <summary>
    ///     Ties those rides again on this map where both are here and free (after a dropped link and its return)
    /// </summary>
    internal static void TieAgain(IEnumerable<(int Rider, int Carried, bool Parasite)> ties)
    {
        foreach (var (riderUid, carriedUid, parasite) in ties) {
            // (a rider loaded from a save may still name the one it carried: the same one, tied again)
            if (EClass._map.FindChara(riderUid) is not { } rider || EClass._map.FindChara(carriedUid) is not { } carried ||
                (carried.host is { } other && other.uid != riderUid) ||
                (parasite ? rider.parasite : rider.ride) is { } taken && taken.uid != carriedUid) {
                continue;
            }

            if (parasite) {
                rider.parasite = carried;
            } else {
                rider.ride = carried;
            }

            carried.host = rider;
            carried._CreateRenderer();
            carried.SetDirtySpeed();
            rider.SetDirtySpeed();
            carried.MoveImmediate(rider.pos, false, false);
            EmpLog.Information("Ride of {Rider} on {Carried} tied again after a dropped link", riderUid, carriedUid);
        }
    }
}
