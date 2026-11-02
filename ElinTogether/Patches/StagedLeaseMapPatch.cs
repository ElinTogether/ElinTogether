using ElinTogether.Models;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A map a player handed back waits beside the save until the host's game is saved, see
///     <see cref="ZoneLeaseState.StageMap" />: in with that save, or as soon as the host loads it itself
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class StagedLeaseMapPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    internal static void OnActivate(Zone __instance)
    {
        ZoneLeaseState.CommitStaged(__instance.uid);
    }
}
