using System.Collections.Generic;
using HarmonyLib;

namespace ElinTogether.Patches;

// hand of 105gun
[HarmonyPatch]
internal class FovCellLightOffsetPatch
{
    internal static readonly HashSet<Fov> PlayerFovs = [];

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Fov), nameof(Fov.ClearVisible))]
    internal static bool OnClearVisible(Fov __instance)
    {
        if (!PlayerFovs.Contains(__instance)) {
            return true;
        }

        // build
        var shared = new HashSet<int>();
        foreach (var fov in PlayerFovs) {
            if (fov != __instance) {
                shared.UnionWith(fov.lastPoints.Keys);
            }
        }

        foreach (var (pos, offset) in __instance.lastPoints) {
            var cell = Fov.map.GetCell(pos);

            cell.light -= offset;
            cell.lightR -= (ushort)(offset * __instance.r / 2);
            cell.lightG -= (ushort)(offset * __instance.g / 2);
            cell.lightB -= (ushort)(offset * __instance.b / 2);

            cell.pcSync = shared.Contains(pos);
        }

        __instance.lastPoints.Clear();
        return false;
    }
}