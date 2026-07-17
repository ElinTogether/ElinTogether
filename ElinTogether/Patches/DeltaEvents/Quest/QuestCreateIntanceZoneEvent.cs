using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class QuestCreateInstanceZoneEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer.FindAllOverrides(typeof(Quest), nameof(Quest.CreateInstanceZone), typeof(Chara));
    }

    [HarmonyPostfix]
    internal static void OnCreateInstanceZone(Quest __instance, Chara c)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsApplying) {
            return;
        }

        client.Delta.AddRemote(new QuestCreateInstanceZoneDelta {
            Uid = __instance.uid,
            Chara = c,
        });
    }
}