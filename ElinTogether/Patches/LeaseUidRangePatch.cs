using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The host never hands out a card uid inside a range leased to a player away (robustness audit 2026-11-01): its
///     counter only moved when a lease came back, so creating more than the headroom on the host during a lease
///     collided with the cards made away
/// </summary>
[HarmonyPatch]
internal static class LeaseUidRangePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardManager), nameof(CardManager.AssignUID))]
    internal static void OnAssignUid(CardManager __instance)
    {
        // a client and a player away allocate in their own range, see ElinNetClient.OnZoneLeaseGrant
        if (NetSession.Instance is not { IsHost: true, IsAway: false }) {
            return;
        }

        ElinNetHost.KeepCounterOutOfLeases(__instance);
    }
}
