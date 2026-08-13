using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CardGenEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(CharaGen), nameof(CharaGen.Create)),
            AccessTools.Method(typeof(ThingGen), nameof(ThingGen._Create)),
        ];
    }

    [HarmonyPostfix]
    internal static void OnCardGenCreate(Card __result)
    {
        // we should relay every single creation call so remotes can hold references
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (connection.IsClient) {
            // pending uid
            __result.uid = PendingUid.GetNext();

            // client zone activation can't null uids
            if (ZoneActivateEvent.IsHappening) {
                return;
            }

            if (PendingContext.IsActive && !CharaProgressCompleteDelta.IsReplaying) {
                CardCache.Add(__result);
                return;
            }

            CardCache.DelayDestroy(__result);
            return;
        }

        // host zone activation include the gen events as snaphsots
        if (ZoneActivateEvent.IsHappening) {
            return;
        }

        // ability fake card
        if (__result is Thing { trait: TraitAbility }) {
            return;
        }

        connection.Delta.AddRemote(CardGenDelta.Create(__result));
    }
}