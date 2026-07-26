using ElinTogether.Models;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.Split))]
internal static class CardSplitEvent
{
    [HarmonyPrefix]
    internal static void OnSplit(Card __instance, ref bool __state)
    {
        __state = __instance.IsResolved;

        if (__state) {
            PendingContext.Enter();
        }
    }

    [HarmonyFinalizer]
    internal static void OnSplitEnd(Card __instance, Thing? __result, bool __state)
    {
        if (!__state) {
            return;
        }

        PendingContext.Exit();

        if (__result is not null && __result != __instance) {
            PendingSplit.Record(__result, __instance);
        }
    }
}