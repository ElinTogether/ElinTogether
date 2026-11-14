using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 17 ("can't sleep", exhausted -820, at another player's base): the game refuses sleep on a map with a
///     quest event going on (Chara.CanSleep: a ZoneEventQuest). In a shared world that event can be another player's
///     quest, one this game does not have (the quest of another story, or one finished meanwhile): it never ends here,
///     and nobody could sleep on that map again. Such an event does not stop sleep; the game's own rule does the rest
/// </summary>
[HarmonyPatch]
internal static class StaleQuestEventSleepPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.CanSleep))]
    internal static void OnCanSleep(Chara __instance, ref bool __result)
    {
        if (__result || NetSession.Instance.Transport is null || EClass._zone?.events.GetEvent<ZoneEventQuest>() is not { } ev ||
            ev.quest is not null) {
            return;
        }

        __result = EClass.debug.godMode || __instance.sleepiness.GetPhase() != 0 || __instance.stamina.GetPhase() <= 1;
    }
}
