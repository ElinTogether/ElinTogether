using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Trait), nameof(Trait.Toggle))]
internal static class CardToggleEvent
{
    [HarmonyPrefix]
    internal static void OnToggle(Trait __instance, out (bool wasOn, ScopeExit r) __state)
    {
        __state = (__instance.owner?.isOn ?? false, MsgRelayContext.Suppress(MsgRelayContext.IsRedirecting));
    }

    [HarmonyPostfix]
    internal static void OnToggleEnd(Trait __instance, bool silent, (bool wasOn, ScopeExit r) __state)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        var owner = __instance.owner;
        if (owner is null || owner.isOn == __state.wasOn) {
            return;
        }

        if (connection.IsClient) {
            if (ElinDelta.IsApplying) {
                return;
            }

            if (PendingUid.IsPending(owner.uid) || !CardCache.Contains(owner)) {
                return;
            }
        }

        // host relay
        connection.Delta.AddRemote(new CardToggleDelta {
            Card = owner,
            IsOn = owner.isOn,
            Silent = silent,
        });
    }

    [HarmonyFinalizer]
    internal static void OnToggleCleanup((bool wasOn, ScopeExit r) __state)
    {
        __state.r?.Dispose();
    }
}