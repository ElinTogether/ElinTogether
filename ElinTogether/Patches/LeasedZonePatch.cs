using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The host cleans up zones nobody needs when it saves (expired fields, quest zones). A zone a player is in
///     is needed, whatever the host's copy of it looks like
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.CanDestroy))]
internal static class LeasedZonePatch
{
    [HarmonyPostfix]
    internal static void OnCanDestroy(Zone __instance, ref bool __result)
    {
        if (!__result) {
            return;
        }

        // GitHub issue 30: a guest's copy of the world deleted an expired dungeon as it came out on the world map
        // (Region.OnActivate), while a friend still fought in it on the host: the dungeon vanished from its map and it
        // could not join. Only the game that keeps the world deletes its places; the copy follows it at the next
        // world copy (an instance, the guest's own temporary map, is still its own to drop)
        if (NetSession.Instance.Transport is ElinNetClient) {
            if (!__instance.IsInstance) {
                __result = false;
            }

            return;
        }

        if (NetSession.Instance.Transport is not ElinNetHost host) {
            return;
        }

        // the whole dungeon: the floors between the top and the one a player is on are its way back
        if (host.IsHeld(__instance)) {
            __result = false;
        }
    }
}
