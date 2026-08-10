using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.ModCharge))]
internal static class CardChargeEvent
{
    [HarmonyPostfix]
    internal static void OnModCharge(Card __instance, int a)
    {
        if (a == 0) {
            return;
        }

        if (NetSession.Instance.Connection is not { IsHost: true } connection) {
            return;
        }

        if (__instance.isDestroyed || !CardCache.Contains(__instance)) {
            return;
        }

        // client will accept later
        connection.Delta.AddRemote(new CardChargeDelta {
            Card = __instance,
            Charges = __instance.c_charges,
        });
    }
}