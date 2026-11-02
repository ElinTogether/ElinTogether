using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The Play tab's "New world" (#56): the game's own new game it started, told to EmpAutoHost; the title's own
///     buttons, clicked by the player, forget the ask. Always patched: all of it happens at the title and before
///     the new world has any session, when a release build has none of the session's patches
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class NewWorldPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Game), nameof(Game.StartNewGame))]
    internal static void OnStartNewGame()
    {
        EmpAutoHost.OnNewGame();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LayerTitle), nameof(LayerTitle.OnClickStart))]
    internal static void OnTitleStart()
    {
        EmpAutoHost.NewWorldAsked = false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LayerTitle), nameof(LayerTitle.OnClickContinue))]
    internal static void OnTitleContinue()
    {
        EmpAutoHost.NewWorldAsked = false;
    }
}
