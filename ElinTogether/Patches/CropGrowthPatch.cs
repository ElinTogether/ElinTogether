using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Host setting "Crop growth" (Workshop comment 2026-11-19: "as overworld time is slow in comparison to player time,
///     it'd be nice if farming was changed in some way for crop growth to be viable"). The game grows a plant once a
///     day at 06:00 (Zone.GrowPlants -> GrowSystem.TryGrow -> Grow(mtp = 1)); a sown crop on a farm field grows mtp
///     times its step here. x3 at most: a watered step of 5 x 2 x 3 is one stage (30), a stage is never skipped. Wild
///     plants, trees and weeds, fertilizer, harvest yield: as in the game. The map's simulating game (the host, or
///     the player away on its own copy) reads the world host's rules
/// </summary>
[HarmonyPatch]
internal static class CropGrowthPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GrowSystem), nameof(GrowSystem.TryGrow))]
    internal static bool OnTryGrow(GrowSystem __instance, VirtualDate date)
    {
        if (NetSession.Instance.Connection is null || NetSession.Instance.Rules.CropGrowth is not (> 1 and var times) ||
            GrowSystem.cell is not { IsFarmField: true } cell || EClass._map?.TryGetPlant(cell) is not { seed: not null }) {
            return true;
        }

        if (__instance.CanGrow(date)) {
            __instance.Grow(System.Math.Min(times, 3));
        }

        return false;
    }
}
