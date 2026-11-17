using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
[HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
internal static class ZoneActivateEvent
{
    internal static bool IsHappening { get; private set; }

    [HarmonyPrefix]
    internal static void OnActivateZone(Zone __instance)
    {
        IsHappening = true;
        TileStateDelta.Forget();
        ActionModeCombat.EnemyVisibility.Clear();
        FovCellLightOffsetPatch.Reset();

        // before the game looks at the date: it would regenerate a cave another player is in, floors included
        if (NetSession.Instance.Transport is ElinNetHost host && host.IsHeld(__instance)) {
            host.KeepAlive(__instance);
        }
    }

    [HarmonyPostfix]
    internal static void OnActivateZoneEnd(Zone __instance)
    {
        IsHappening = false;
        if (NetSession.Instance.Connection is not null) {
            CardCache.CacheCurrentZone();
        }

        // before anyone acts: the game puts back every character whose zone this is
        ElinNetHost.RemoveUnplayedCharas();

        Helper.PublicLitter.Sweep(__instance);

        // we are not host
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        host.SweepStaleCellEntries();

        // every zone activate should be relayed to clients
        // broadcast all map assets to clients when host finishes map loading
        CoroutineHelper.Deferred(() => host.PropagateZoneChangeState(__instance), 2);
    }

    [HarmonyFinalizer]
    internal static void OnActivateZoneFinalize()
    {
        IsHappening = false;
    }
}