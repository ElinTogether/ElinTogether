using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(TraitPowerStatue), nameof(TraitPowerStatue.OnUse))]
internal static class TraitPowerStatueUseEvent
{
    [HarmonyPrefix]
    internal static void OnShrineUse(TraitPowerStatue __instance, out bool __state)
    {
        __state = __instance.owner.isOn;
    }

    [HarmonyPostfix]
    internal static void OnShrineUseEnd(TraitPowerStatue __instance, bool __state)
    {
        if (!__state || __instance.owner.isOn) {
            return;
        }

        // host only
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        host.Delta.AddRemote(new CardShrineUsedDelta {
            Card = __instance.owner,
        });
    }
}