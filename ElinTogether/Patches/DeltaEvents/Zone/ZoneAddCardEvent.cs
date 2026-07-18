using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class ZoneAddCardEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.AddCard), typeof(Card), typeof(int), typeof(int))]
    internal static bool OnAddCardToZone(Zone __instance, Card t, int x, int z)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return true;
        }

        if (NetSession.Instance.IsClient && !CardCache.Contains(t)) {
            return false;
        }

        // only host can propagate add card event to remotes
        var card = RemoteCard.Create(t, true);
        connection.Delta.AddRemote(new ZoneAddCardDelta {
            Card = card,
            ZoneUid = __instance.uid,
            Pos = new() { X = x, Z = z },
        });

        return true;
    }
}