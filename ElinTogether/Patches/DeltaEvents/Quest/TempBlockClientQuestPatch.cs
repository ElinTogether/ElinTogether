using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class TempBlockClientQuestPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer.FindAllOverrides(typeof(Quest), nameof(Quest.OnClickQuest));
    }

    [HarmonyPrefix]
    internal static bool OnClientClickQuest()
    {
        if (NetSession.Instance.IsClient) {
            EmpPop.Information("emp_ui_quest_client".lang());
            return false;
        }

        return true;
    }
}