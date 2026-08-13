using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Thing), nameof(Thing.Identify))]
internal static class CardIdentifyEvent
{
    [HarmonyPrefix]
    internal static void OnIdentify(Thing __instance, out int __state)
    {
        __state = __instance.c_IDTState;
    }

    [HarmonyPostfix]
    internal static void OnIdentifyEnd(Thing __instance, IDTSource idtSource, int __state)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (__instance.isDestroyed || __instance.c_IDTState == __state) {
            return;
        }

        if (connection.IsClient) {
            // client waits for host delta
            if (ElinDelta.IsApplying || PendingUid.IsPending(__instance.uid)) {
                return;
            }
        }

        if (!CardCache.Contains(__instance)) {
            return;
        }

        connection.Delta.AddRemote(new CardIdentifyDelta {
            Card = __instance,
            State = __instance.c_IDTState,
            Source = (byte)idtSource,
        });
    }
}