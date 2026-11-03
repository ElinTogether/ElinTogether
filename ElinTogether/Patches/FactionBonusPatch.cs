using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     The faction bonus (pc.faction.charaElements, "Faction" in a stat's note): the faction-wide elements of what
///     the members of the player's faction wear, a god's artifact for one. The game keeps it step by step, added when
///     a member puts one on or joins, taken out when it takes it off or leaves, and builds it again from the members
///     at each load (it is not saved). Here a character has copies, swapped as its player travels, and whether a
///     god's item counts depends on its owner's faith (OtherPlayerPatch.OnIsEffective), which changes with the party
///     it is in: a step taken out on one copy or under one owner had not always been added on that copy or under
///     that owner, and the bonus drifted, below zero (GitHub issue 73: Strength "Faction -13" on the host after a
///     player with Opatos' hammer came and went). With other players, the bonus is what a load gives: counted again
///     from the members' gear after each change, and every few seconds for the copies swapped in without one
/// </summary>
[HarmonyPatch]
internal static class FactionBonusPatch
{
    private const float RecountSeconds = 5f;

    private static bool _dirty;
    private static float _next;

    private static bool IsMultiplayer => NetSession.Instance.Transport is not null;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ElementContainerFaction), nameof(ElementContainerFaction.OnEquip), typeof(Thing))]
    internal static bool OnEquip()
    {
        return Defer();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ElementContainerFaction), nameof(ElementContainerFaction.OnUnequip), typeof(Thing))]
    internal static bool OnUnequip()
    {
        return Defer();
    }

    private static bool Defer()
    {
        if (!IsMultiplayer) {
            return true;
        }

        _dirty = true;
        return false;
    }

    /// <summary>
    ///     Each frame of a session (ElinNetBase.Update): counted again when something changed, or now and then
    /// </summary>
    internal static void Tick()
    {
        if (!_dirty && Time.realtimeSinceStartup < _next) {
            return;
        }

        if (EClass.core is not { IsGameStarted: true } || EClass.game is not { isLoading: false } || EClass.pc?.faction is null) {
            return;
        }

        _dirty = false;
        _next = Time.realtimeSinceStartup + RecountSeconds;
        Recount();
    }

    /// <summary>
    ///     What FactionBranch.SetOwner adds at a load: the gear of every member of every base of the faction, and of
    ///     the party (players and companions without a base), each character once, its copy the world knows
    /// </summary>
    internal static void Recount()
    {
        var faction = EClass.pc.faction;
        var bonus = faction.charaElements;
        var sums = new Dictionary<int, (int Value, int Exp)>();
        var seen = new HashSet<int>();

        foreach (var branch in faction.GetChildren()) {
            foreach (var member in branch.members) {
                Count(member);
            }
        }

        foreach (var member in EClass.pc.party.members) {
            Count(member);
        }

        foreach (var id in new List<int>(sums.Keys)) {
            if (sums[id].Value == 0) {
                sums.Remove(id);
            }
        }

        var changed = bonus.dict.Count != sums.Count;
        foreach (var (id, sum) in sums) {
            changed |= bonus.dict.TryGetValue(id, out var element) is false || element.vBase != sum.Value;
        }

        if (!changed) {
            return;
        }

        EmpLog.Debug("Faction bonus counted again: {Before} -> {After}", Describe(bonus), string.Join(", ", sums.Select(s => $"{s.Key}:{s.Value.Value}")));
        bonus.dict.Clear();
        foreach (var (id, sum) in sums) {
            bonus.ModBase(id, sum.Value).vExp = sum.Exp;
        }

        // the members' stats with it, as the game does after a change (ElementContainerFaction.CheckDirty)
        bonus.isDirty = true;
        bonus.CheckDirty();
        return;

        void Count(Chara? chara)
        {
            if (chara is null || chara.isDestroyed || chara.faction != faction ||
                (EClass.game.cards.globalCharas.TryGetValue(chara.uid, out var known) && known != chara) || !seen.Add(chara.uid)) {
                return;
            }

            foreach (var slot in chara.body.slots) {
                if (slot.thing is not { } thing || !bonus.IsEffective(thing)) {
                    continue;
                }

                foreach (var element in thing.elements.dict.Values) {
                    if (!element.IsGlobalElement) {
                        continue;
                    }

                    var sum = sums.GetValueOrDefault(element.id);
                    sums[element.id] = (sum.Value + element.Value, element.vExp);
                }
            }
        }
    }

    private static string Describe(ElementContainerFaction bonus)
    {
        var parts = new List<string>();
        foreach (var element in bonus.dict.Values) {
            parts.Add($"{element.id}:{element.vBase}");
        }

        return string.Join(", ", parts);
    }
}
