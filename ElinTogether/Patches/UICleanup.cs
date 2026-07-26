using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class UICleanup
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UI), nameof(UI.OnUpdate))]
    internal static void ClearStaleDragImage(UI __instance)
    {
        if (ELayer.game is null && __instance.hud is not null && __instance.hud.imageDrag.gameObject.activeSelf) {
            __instance.hud.SetDragImage(null);
        }
    }
}
