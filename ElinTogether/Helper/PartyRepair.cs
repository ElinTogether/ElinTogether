using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     A party knows its leader by uid only (Party.uidLeader), and the game looks an unknown one up on the current map
///     (RefChara.GetAndCache): with no map yet, while a world loads, that throws. A player's character uploaded from
///     its own game carries its own copy of the party (ElinNetHost.ReplaceRemoteChara), led by the host's character
///     of that time: on 2026-10-30 the public server gave the world's character back at stop and destroyed its
///     stand-in, a guest's copy still said the stand-in led it, and the next start threw in Zone.OnLoad
///     (FactionBranch.SetOwner, RefreshEfficiency, Chara.IsPCParty, Party.leader) and stayed on the title screen.
///     <see cref="Repoint" /> moves every party from the character that leaves the local seat to the one that takes it;
///     <see cref="RepairOnLoad" /> repairs a world saved before this, in every game (host, guest, server)
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class PartyRepair
{
    private static bool _warnedLookup;

    /// <summary>
    ///     Every party object of the world: the global characters' and those of the loaded maps
    /// </summary>
    private static IEnumerable<Party> AllParties()
    {
        var game = EClass.core?.game;
        if (game?.cards?.globalCharas is not { } globals) {
            return [];
        }

        var charas = globals.Values.AsEnumerable();
        if (game.spatials?.map is { } spatials) {
            charas = charas.Concat(spatials.Values.OfType<Zone>().Where(z => z.map?.charas is not null)
                .SelectMany(z => z.map.charas));
        }

        return charas.Where(c => c?.party is not null).Select(c => c.party).Distinct();
    }

    /// <summary>
    ///     Every party led by <paramref name="from" />, or listing it, is led by or lists <paramref name="to" />
    ///     instead. Returns how many parties changed
    /// </summary>
    internal static int Repoint(int from, Chara? to)
    {
        if (from == 0 || to is null || to.uid == from) {
            return 0;
        }

        var changed = 0;
        foreach (var party in AllParties().ToList()) {
            var touched = false;
            if (party.uidLeader == from) {
                party.leader = to;
                touched = true;
            }

            var index = party.uidMembers.IndexOf(from);
            if (index >= 0) {
                if (party.uidMembers.Contains(to.uid)) {
                    party.uidMembers.RemoveAll(uid => uid == from);
                } else {
                    party.uidMembers[index] = to.uid;
                    party.uidMembers.RemoveAll(uid => uid == from);
                }

                // rebuilt from the uids when next read
                party._members = null;
                touched = true;
            }

            if (touched) {
                changed++;
            }
        }

        if (changed > 0) {
            EmpLog.Information("Parties: {Count} led by or listing {From} now led by or listing {To}", changed, from, to.uid);
        }

        return changed;
    }

    /// <summary>
    ///     Right after the world is read, before any zone loads (Zone.OnLoad runs inside Game.OnLoad): a party whose
    ///     leader is no character of the world is led by the player's character when it is one of its members, else
    ///     by its first known member, else by the character that holds it
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Game), nameof(Game.OnLoad))]
    internal static void RepairOnLoad(Game __instance)
    {
        try {
            RepairUnknownLeaders(__instance);
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "Parties: the repair of unknown leaders on load failed");
        }
    }

    internal static int RepairUnknownLeaders(Game game)
    {
        if (game?.cards?.globalCharas is not { } globals) {
            return 0;
        }

        var playerUid = game.player?.uidChara ?? 0;
        var repaired = 0;
        foreach (var holder in globals.Values.Where(c => c?.party is not null).ToList()) {
            var party = holder.party;
            if (party.uidLeader == 0 || globals.ContainsKey(party.uidLeader)) {
                continue;
            }

            var lost = party.uidLeader;
            var leader = party.uidMembers.Contains(playerUid) && globals.TryGetValue(playerUid) is { } pc
                ? pc
                : party.uidMembers.Select(uid => globals.TryGetValue(uid)).FirstOrDefault(c => c is not null) ?? holder;
            party.leader = leader;
            repaired++;
            EmpLog.Warning("Parties: the leader {Lost} of the party of {Holder} is no character of the world, {Leader} leads it",
                lost, holder.uid, leader.uid);
        }

        return repaired;
    }

    /// <summary>
    ///     The last guard: an unknown uid looked up with no map (a world being read) is nobody, not a throw
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(RefChara), nameof(RefChara.GetAndCache))]
    internal static bool OnLookupWithoutMap(RefChara __instance, int uid, ref Chara? __result)
    {
        if (uid == 0 || EClass.core?.game is not { } game || game.activeZone?.map is not null ||
            (__instance.chara is { } cached && (cached.IsGlobal || cached.IsAliveInCurrentZone)) ||
            game.cards?.globalCharas?.ContainsKey(uid) is not false) {
            return true;
        }

        if (!_warnedLookup) {
            _warnedLookup = true;
            EmpLog.Warning("Parties: character {Uid} looked up with no map loaded is no character of the world: nobody", uid);
        }

        __instance.chara = null;
        __result = null;
        return false;
    }
}
