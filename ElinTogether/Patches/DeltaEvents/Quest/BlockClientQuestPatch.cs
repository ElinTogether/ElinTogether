using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class BlockClientQuestPatch
{
    internal static bool CanClientAccept(Quest quest)
    {
        // TODO: drama quest (main, home, zone)
        return quest is { uid: >= 0, IsRandomQuest: true, UseInstanceZone: false } && quest.source.drama.IsEmpty();
    }

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer.FindAllOverrides(typeof(Quest), nameof(Quest.OnClickQuest));
    }

    [HarmonyPrefix]
    internal static bool OnClientClickQuest(Quest __instance)
    {
        if (!NetSession.Instance.IsClient || CanClientAccept(__instance)) {
            return true;
        }

        EmpPop.Information("emp_ui_quest_client".lang());
        return false;
    }
}

[HarmonyPatch]
internal static class ClientQuestInstanceZoneGate
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer.FindAllOverrides(typeof(Quest), nameof(Quest.CreateInstanceZone), typeof(Chara));
    }

    [HarmonyPrefix]
    internal static bool OnClientCreateInstanceZone(Quest __instance, ref Zone? __result)
    {
        if (!NetSession.Instance.IsClient) {
            return true;
        }

        __result = null;
        EmpLog.Debug("Blocking client instance zone creation {QuestUid} {QuestId}", __instance.uid, __instance.id);
        return false;
    }
}