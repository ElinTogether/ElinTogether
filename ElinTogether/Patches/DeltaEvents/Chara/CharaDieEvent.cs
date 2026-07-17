using System;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
internal static class CharaDieEvent
{
    [HarmonyPrefix]
    internal static bool OnCharaDie(Chara __instance, Element? e, Card? origin, AttackSource attackSource, Chara? originalTarget)
    {
        switch (NetSession.Instance.Connection) {
            case ElinNetHost host when !ElinDelta.IsApplying:
                host.Delta.DeferRemote(new CharaDieDelta {
                    Owner = __instance,
                    ElementId = e?.id,
                    Origin = origin,
                    AttackSource = attackSource,
                    OriginalTarget = originalTarget,
                });

                return true;
            case ElinNetClient when !ElinDelta.IsApplying:
                // we are clients, drop the update and wait for delta
                return false;
            default:
                return true;
        }
    }
}