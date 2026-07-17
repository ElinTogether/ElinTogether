using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class QuestCompleteEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Quest), nameof(Quest.Complete))]
    internal static void OnQuestComplete(Quest __instance)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        connection.Delta.AddRemote(new QuestCompleteDelta {
            Uid = __instance.uid,
        });
    }
}