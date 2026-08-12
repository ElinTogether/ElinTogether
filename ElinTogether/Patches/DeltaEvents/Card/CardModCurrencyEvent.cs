using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CardModCurrencyEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.ModCurrency))]
    internal static void OnModCurrency(Card __instance, int a, string id)
    {
        if (a == 0) {
            return;
        }

        // ThingRequest replay is the client predicting, not applying
        if (NetSession.Instance.Connection is not ElinNetClient client ||
            (ElinDelta.IsApplying && !ThingRequest.IsApplying)) {
            return;
        }

        if (!__instance.IsPC) {
            return;
        }

        client.Delta.AddRemote(new CardModCurrencyDelta {
            Amount = a,
            CurrencyId = id,
        });
    }
}