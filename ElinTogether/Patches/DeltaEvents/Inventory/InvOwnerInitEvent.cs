using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(InvOwner), nameof(InvOwner.Init))]
internal static class InvOwnerInitEvent
{
    [HarmonyPrefix]
    internal static void OnInit(InvOwner __instance, out ElinDelta.PatchScope __state)
    {
        // client effect windows is fake inv card, uses pending scope
        __state = ElinDelta.PatchScope.Pending(__instance is InvOwnerEffect &&
                                               __instance.owner is null &&
                                               NetSession.Instance.Connection is { IsClient: true });
    }

    [HarmonyFinalizer]
    internal static void OnInitEnd(ElinDelta.PatchScope __state)
    {
        __state.Exit();
    }
}

[HarmonyPatch(typeof(LayerDragGrid), nameof(LayerDragGrid.OnKill))]
internal static class LayerDragGridKillEvent
{
    [HarmonyPostfix]
    internal static void OnKill(LayerDragGrid __instance)
    {
        if (NetSession.Instance.Connection is not { IsClient: true }) {
            return;
        }

        if (__instance.owner?.owner is not { } card || !PendingUid.IsPending(card.uid) || card.parent is not null) {
            return;
        }

        CardCache.Remove(card.uid);
        CardCache.DelayDestroy(card);
    }
}