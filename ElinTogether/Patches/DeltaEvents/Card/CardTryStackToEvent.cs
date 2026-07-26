using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.TryStackTo))]
internal static class CardTryStackEvent
{
    [HarmonyPrefix]
    internal static bool OnCardTryStackTo(Card __instance, Thing to, ref bool __result)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return true;
        }

        if (to.IsHostOwned != __instance.IsHostOwned) {
            return false;
        }

        if (!to.IsHostOwned) {
            return true;
        }

        if (!__instance.CanStackTo(to)) {
            return false;
        }

        __result = true;
        client.Delta.AddRemote(new CardTryStackToDelta {
            Card = __instance,
            To = to,
            Parent = to.parent as Card,
        });

        return false;
    }
}