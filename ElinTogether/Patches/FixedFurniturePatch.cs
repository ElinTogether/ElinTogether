using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     In a game with other players, the home crystal (TraitCoreZone) and the quest board of the Meadow, the start land
///     everyone shares, stay where they are: the move mode (AM_MoveInstalled) and the empty hand's pick (Trait.CanBeHeld,
///     HotItemNoItem) cannot take them (lead dev on the public server, 2026-11-10: only the Meadow's, the other lands'
///     are their owners' to move). A solo game keeps the game's own rule
/// </summary>
[HarmonyPatch]
internal static class FixedFurniturePatch
{
    internal static bool IsFixed(Card? card)
    {
        return card?.trait is TraitCoreZone or TraitQuestBoard && NetSession.Instance.Connection is not null &&
               EClass._zone is { } here && here == EClass.game.StartZone;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AM_MoveInstalled), nameof(AM_MoveInstalled.GetTarget))]
    internal static void NotAMoveTarget(ref Card? __result)
    {
        if (IsFixed(__result)) {
            __result = null;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Trait), nameof(Trait.CanBeHeld), MethodType.Getter)]
    internal static void NotHeld(Trait __instance, ref bool __result)
    {
        if (__result && IsFixed(__instance.owner)) {
            __result = false;
        }
    }
}
