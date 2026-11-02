using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Compatibility with Dynamic Riding (Workshop 3548942723), which draws the creature a character rides: it
///     replaces the idle pictures of the shared "ride" body by a table of a single one. The next actor made for a
///     mount (the mount enters the screen: once after a save is loaded when playing alone, at every world or map
///     reload in a session) read a picture by direction in that table and threw in the middle of the frame's
///     drawing (IndexOutOfRangeException, or a null picture right after). The one picture there is, is shown; the
///     mod sets the right one on its next frame
/// </summary>
[HarmonyPatch(typeof(SpriteProvider), nameof(SpriteProvider.SetSpriteIdle))]
[AlwaysPatched]
internal static class RideSpriteGuardPatch
{
    [HarmonyPrefix]
    internal static bool OnSetSpriteIdle(SpriteProvider __instance)
    {
        if (__instance.vCurrent?.idle is not { Length: > 0 } idle || __instance.currentDir < idle.GetLength(0)) {
            return true;
        }

        __instance.onSetSprite?.Invoke(idle[0, 0]);
        return false;
    }
}
