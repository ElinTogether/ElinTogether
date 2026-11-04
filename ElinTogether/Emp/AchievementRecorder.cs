#if DEBUG
using System.Collections.Generic;
using HarmonyLib;

namespace ElinTogether.Emp;

/// <summary>
///     Test bridge only: Steam achievements are recorded here and never reach the real Steam account of the bench.
///     It runs last (after PlayerAchievements, which may send the call to another player's game) and skips the game's
///     own Steam.GetAchievement, so what the bench observes is "this game made the call" without unlocking anything
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
public static class AchievementRecorder
{
    /// <summary>
    ///     The ids the game called, in order (read by the tests through eval: AchievementRecorder.Dump())
    /// </summary>
    private static readonly List<string> Calls = [];

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(global::Steam), nameof(global::Steam.GetAchievement))]
    private static bool Record(ID_Achievement id, bool __runOriginal)
    {
        // Harmony runs this after a prefix that skipped the original: a call PlayerAchievements sent to another
        // player's game is not this game's, so it is not recorded here
        if (!__runOriginal) {
            return false;
        }

        lock (Calls) {
            Calls.Add(id.ToString());
        }

        return false;
    }

    public static string Dump()
    {
        lock (Calls) {
            return string.Join(",", Calls);
        }
    }

    public static void Clear()
    {
        lock (Calls) {
            Calls.Clear();
        }
    }
}
#endif
