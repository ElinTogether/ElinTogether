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
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        if (ElinDelta.IsApplying) {
            // host running remote progress during apply context
            // pack backer loot, treasure chest into the batch
            if (CharaProgressCompleteEvent.ShouldPack(false) && __instance.IsActiveZone && !t.isDestroyed) {
                CharaProgressCompleteEvent.Pack(new ZoneAddCardDelta {
                    Card = RemoteCard.Create(t),
                    ZoneUid = __instance.uid,
                    Pos = new() { X = x, Z = z },
                });
            }

            return true;
        }

        if (connection.IsClient && !CardCache.Contains(t)) {
            CardCache.DelayDestroy(t);
            return true;
        }

        if (!__instance.IsActiveZone) {
            return true;
        }

        // only host can propagate add card event to remotes
        var card = RemoteCard.Create(t);
        connection.Delta.AddRemote(new ZoneAddCardDelta {
            Card = card,
            ZoneUid = __instance.uid,
            Pos = new() { X = x, Z = z },
        });

        return true;
    }
}