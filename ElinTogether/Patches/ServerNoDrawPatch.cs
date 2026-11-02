using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     A dedicated server started without graphics has no instancing: every frame the map's mesh passes threw
///     "Instancing is not supported" and its log grew by about 30 MB an hour (2026-10-30). Nobody looks at its screen:
///     it draws nothing there.
///     The game still fills the passes every frame and only MeshPass.Draw puts their write position back to zero.
///     Skipping Draw without that reset made each pass add a new 1023-entry MeshBatch (~80 KB) every few frames, forever:
///     the idle public server grew by ~1.4 GB an hour (2026-11-01, "#pass New Batch pass tile_floor/2705/1023" in its log)
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class ServerNoDrawPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MeshPass), nameof(MeshPass.Draw))]
    internal static bool SkipOnServer(MeshPass __instance)
    {
        if (!EmpServer.Requested || SystemInfo.supportsInstancing) {
            return true;
        }

        Rewind(__instance, 0);
        return false;
    }

    // What Draw does to the pass and to the passes it draws itself, minus the drawing
    private static void Rewind(MeshPass pass, int depth)
    {
        if (pass == null || depth > 4) {
            return;
        }

        if (pass.haveSnowPass) {
            Rewind(pass.snowPass, depth + 1);
        }

        if (pass.haveSubPass) {
            Rewind(pass.subPass, depth + 1);
        }

        if (pass.haveShadowPass) {
            Rewind(pass.shadowPass, depth + 1);
        }

        pass.DrawEmpty();
    }
}
