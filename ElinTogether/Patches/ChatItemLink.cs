using ElinTogether.Components;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Shift+click on a thing of the inventory while the Chat box is being written in: a link to that thing goes in
///     the line (TabChat.TryLink) instead of the game's Shift+click (moving a stack). Any other time, the game's
/// </summary>
[HarmonyPatch]
internal static class ChatItemLink
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InvOwner), nameof(InvOwner.CanShiftClick))]
    internal static bool OnCanShiftClick(ButtonGrid b, bool rightMouse, ref bool __result)
    {
        if (rightMouse || !TabChat.WouldLink(b?.card as Thing)) {
            return true;
        }

        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InvOwner), nameof(InvOwner.OnShiftClick))]
    internal static bool OnShiftClick(ButtonGrid b, bool rightMouse)
    {
        return rightMouse || !TabChat.TryLink(b?.card as Thing);
    }
}
