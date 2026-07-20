using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class CardSplitEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.Split))]
    internal static void OnClientSplit(Card __instance, Thing __result)
    {
        if (__instance == __result) {
            return;
        }

        // we use negative uid to mark the parent stack
        if (NetSession.Instance.IsClient) {
            __result.uid = -__instance.uid;
        }
    }
}