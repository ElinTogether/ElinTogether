using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class CardSplitEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.Split))]
    internal static void OnClientSplit(Card __instance, Thing __result, int a)
    {
        if (__instance == __result) {
            return;
        }

        if (NetSession.Instance.IsClient) {
            __result.SetSplitContext(__instance, a);
        }
    }
}