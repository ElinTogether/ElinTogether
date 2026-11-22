using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     GitHub issue 82: an empty entry (null) got into the quest log after the host accepted a quest a guest had just
///     accepted from the same board. Every quest message then threw (SharedQuests.Find), and the save no longer
///     loaded (Game.ApplyFix reads each quest's id). Empty entries are taken out when a world loads, and as soon as
///     a quest message finds one
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class QuestListRepair
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Game), nameof(Game.OnLoad))]
    internal static void RepairOnLoad(Game __instance)
    {
        Clean(__instance.quests, "load");
    }

    /// <summary>
    ///     Takes the empty entries out of the quest log and the offers; true when there were some
    /// </summary>
    internal static bool Clean(QuestManager? quests, string when)
    {
        if (quests is null) {
            return false;
        }

        var gone = (quests.list?.RemoveAll(q => q is null) ?? 0) + (quests.globalList?.RemoveAll(q => q is null) ?? 0);
        if (gone > 0) {
            EmpLog.Warning("Quest log: {Count} empty entr(ies) taken out ({When})", gone, when);
        }

        return gone > 0;
    }
}
