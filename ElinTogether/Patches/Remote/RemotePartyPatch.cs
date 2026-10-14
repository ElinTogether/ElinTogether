using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class RemotePartyPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.IsPCParty), MethodType.Getter)]
    internal static bool OnGetPcParty(Chara __instance, ref bool __result)
    {
        __result = __instance.party is { } party && party.members.Contains(__instance);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Party), nameof(Party.RemoveMember))]
    internal static bool OnRemoveRemoteParty(Party __instance, Chara c)
    {
        return !NetSession.Instance.HasActiveConnection || !c.IsPlayer;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.MaxAlly), MethodType.Getter)]
    internal static void OnGetRemoteMaxAlly(Player __instance, ref int __result)
    {
        if (NetSession.Instance.Connection is ElinNetClient && EClass.pc.party?.leader is { IsPC: false } leader) {
            __result = Mathf.Min(Mathf.Max(leader.CHA / 10, 1), 5) + leader.Evalue(FEAT.featParty);
        }

        __result = Mathf.Max(__result, NetSession.Instance.CurrentPlayers.Count);
    }
}