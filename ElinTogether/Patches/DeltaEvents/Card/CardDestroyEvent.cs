using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CardDestroyEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.Destroy))]
    internal static void OnDestroy(Card __instance)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (!CardCache.Contains(__instance)) {
            return;
        }

        // delta will be sent in CardModNumEvent
        if (__instance.Num <= 0) {
            return;
        }

        // client pending uid
        if (connection.IsClient && PendingUid.IsPending(__instance.uid)) {
            return;
        }

        // client replay delta list
        if (connection.IsClient && ElinDelta.IsApplying) {
            return;
        }

        connection.Delta.AddRemote(new CardModNumDelta {
            Card = __instance,
            Num = 0,
        });
    }
}