using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 30 (a scroll of return took a guest to the world map): the game counts the return down in the
///     player's Chara.Tick, idle ticks included. Alone, an idle player stops the world (AM_Adv.ShouldPauseGame), so
///     only its own acts (and the one idle tick after each) count, and the list of places asks before any turn goes.
///     In company the world runs while the others play and menus do not pause it (PauseGame): the idle player ticked
///     on, the 10 to 20 turns ran out while the list of places was still open, and with no place chosen the game
///     sends the player to the world map (Chara.Tick: uidDest 0). Here, as alone: no turn counts while the list
///     still asks, and of a run of idle ticks only the first counts
/// </summary>
[HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
internal static class ReturnScrollTurnsPatch
{
    // the last tick of the local player was an idle one
    private static bool _lastIdle;

    [HarmonyPrefix]
    internal static void Before(Chara __instance, out (Player.ReturnInfo? Info, int Turns, bool Keep) __state)
    {
        __state = default;
        if (!__instance.IsPC || !NetSession.Instance.InGame) {
            return;
        }

        // the game's own test of an idle tick, see Chara.Tick
        var idle = (__instance.HasNoGoal || __instance.ai is not { IsRunning: true }) && !__instance.WillConsumeTurn();
        var keep = idle && _lastIdle;
        _lastIdle = idle;

        if (EClass.player.returnInfo is not { } info || !(keep || info.askDest)) {
            return;
        }

        // raised before the game counts it down: at 0 it would already have sent the player away
        info.turns++;
        __state = (info, info.turns, true);
    }

    [HarmonyPostfix]
    internal static void After((Player.ReturnInfo? Info, int Turns, bool Keep) __state)
    {
        // the game did not reach its count (the tick was skipped or ended early): the raise is taken back
        if (__state is { Keep: true, Info: { } info } && EClass.player.returnInfo == info && info.turns == __state.Turns) {
            info.turns--;
        }
    }
}
