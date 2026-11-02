using ElinTogether.Components;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The game turns the wheel over a window's tab row into the next tab (Scene.OnUpdate, Window.NextTab): on the
///     Multiplayer menu it changed the page while the player only meant to scroll (user 2026-11-02). Its tabs change by
///     a click only; the game's own windows keep it
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class MenuWheelTabPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Window), nameof(Window.NextTab))]
    internal static bool OnNextTab(Window __instance, ref bool __result)
    {
        if (__instance && __instance.GetComponentInParent<LayerElinTogether>()) {
            __result = false;
            return false;
        }

        return true;
    }
}
