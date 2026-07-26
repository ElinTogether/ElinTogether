using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.ModNum))]
internal static class CardModNumEvent
{
    [HarmonyPrefix]
    internal static bool OnCardModNum(Card __instance, ref int a)
    {
        if (CardModNumDelta.IsApplying || !__instance.IsResolved) {
            return true;
        }

        a = 0;
        return false;
    }

    [HarmonyPostfix]
    internal static void OnCardModNumEnd(Card __instance, int a)
    {
        if (NetSession.Instance.Connection is not { } connection || a == 0 || CardModNumDelta.IsApplying) {
            return;
        }

        if (!CardCache.Contains(__instance)) {
            return;
        }

        if (connection.IsClient && PendingUid.IsPending(__instance.uid)) {
            return;
        }

        var delta = new CardModNumDelta {
            Card = __instance,
            Num = __instance.Num,
        };

        if (CharaProgressCompleteEvent.IsHappening && NetSession.Instance.IsHost) {
            CharaProgressCompleteEvent.DeltaList.Add(delta);
            return;
        }

        connection.Delta.AddRemote(delta);
    }
}