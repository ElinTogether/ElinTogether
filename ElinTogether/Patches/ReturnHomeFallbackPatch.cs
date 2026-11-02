using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The places a scroll of return offers (SpatialManager.ListReturnLocations) are the player's home, the deepest
///     floors of the dungeons it went down and the zones tagged "return". A guest's character comes with no home
///     (CharaImport.Adopt clears it, OwnSettings puts back only one the guest set itself), where the host's has its start
///     site: away from it the host could go home, a guest read "returnNowhere". As for its respawn, a player without a
///     home has the start site (or the last town, <see cref="OwnSettings.RespawnFallback" />) in the list
/// </summary>
[HarmonyPatch(typeof(SpatialManager), nameof(SpatialManager.ListReturnLocations))]
internal static class ReturnHomeFallbackPatch
{
    [HarmonyPostfix]
    internal static void OnListReturnLocations(List<Zone> __result)
    {
        // (in a survival game the game offers nefias only, the home included)
        if (NetSession.Instance.Connection is null || EClass.game.IsSurvival || EClass.pc is not { homeZone: null } ||
            OwnSettings.RespawnFallback() is not { } home || home == EClass._zone || __result.Contains(home)) {
            return;
        }

        __result.Add(home);
        __result.Sort((a, b) => a.GetSortVal() - b.GetSortVal());
    }
}
