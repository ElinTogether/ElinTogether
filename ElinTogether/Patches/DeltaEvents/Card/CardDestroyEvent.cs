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
            // (a spell taken off the belt alone on a map of its own: the layout is told to the world, issues 83, 85, 86)
            if (__instance is Thing { trait: TraitAbility } && __instance.GetRootCard() == EClass.pc && NetSession.Instance.IsAway) {
                CardAddThingEvent.AbilityLayoutDirty = true;
            }

            return;
        }

        if (!CardCache.Contains(__instance)) {
            return;
        }

        // delta will be sent in CardModNumEvent
        if (__instance.Num <= 0) {
            return;
        }

        // ability fake card
        if (__instance is Thing { trait: TraitAbility }) {
            if (connection.IsClient) {
                CardAddThingEvent.AbilityLayoutDirty = true;
            }

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

        // a client copy of a character going away is never authoritative, see CardModNumDelta
        if (connection.IsClient && __instance is Chara chara) {
            EmpLog.Warning("Client copy of chara {Uid} destroyed, not synced {StackTrace}",
                chara.uid, System.Environment.StackTrace);
            return;
        }

        connection.Delta.AddRemote(new CardModNumDelta {
            Card = __instance,
            Num = 0,
        });
    }
}