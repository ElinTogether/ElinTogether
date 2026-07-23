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
        __result = __instance.party is { } party && party.members.Contains(__instance);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Party), nameof(Party.RemoveMember))]
    internal static bool OnRemoveRemoteParty(Party __instance, Chara c)
    {
        return !NetSession.Instance.HasActiveConnection || !c.IsPlayer;
    }
}