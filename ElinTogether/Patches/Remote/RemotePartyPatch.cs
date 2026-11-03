using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class RemotePartyPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.IsPCParty), MethodType.Getter)]
    internal static bool OnGetPcParty(Chara __instance, ref bool __result)
    {
        // (asked many times a turn for every character: a plain loop by reference, which is what List.Contains does
        // for a Chara, without the comparer's calls; perf batch 2026-11-03)
        __result = false;
        if (__instance.party is { } party) {
            var members = party.members;
            for (var i = 0; i < members.Count; i++) {
                if (ReferenceEquals(members[i], __instance)) {
                    __result = true;
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     The game only asks "is its party this one?" before adding a member. A character that came whole from
    ///     another game carries its own copy of the party: already in the list here, it was added a second time,
    ///     and the roster of the screen threw at every refresh ("An item with the same key has already been
    ///     added", seen in a real game on 2026-10-10)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Party), nameof(Party.AddMemeber))]
    internal static bool OnAddMemberTwice(Party __instance, Chara c)
    {
        if (c is null || c.party == __instance || !__instance.members.Contains(c)) {
            return true;
        }

        c.party = __instance;
        if (!__instance.uidMembers.Contains(c.uid)) {
            __instance.uidMembers.Add(c.uid);
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Party), nameof(Party.RemoveMember))]
    internal static bool OnRemoveRemoteParty(Party __instance, Chara c)
    {
        return !NetSession.Instance.HasActiveConnection || !c.IsPlayer;
    }
}