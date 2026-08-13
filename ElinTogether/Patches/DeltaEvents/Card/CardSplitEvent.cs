using ElinTogether.Models;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.Split))]
internal static class CardSplitEvent
{
    [HarmonyPrefix]
    internal static void OnSplit(Card __instance, out ElinDelta.PatchScope __state)
    {
        __state = ElinDelta.PatchScope.Pending(__instance.IsResolved);
    }

    [HarmonyFinalizer]
    internal static void OnSplitEnd(Card __instance, Thing? __result, ElinDelta.PatchScope __state)
    {
        __state.Exit();

        if (!__state.IsActive) {
            return;
        }

        if (__result is not null && __result != __instance) {
            PendingSplit.Record(__result, __instance);
        }
    }
}