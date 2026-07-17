using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;

[HarmonyPatch]
internal static class QuestCreateEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.UpdateQuests))]
    internal static bool OnUpdateQuests()
    {
        return NetSession.Instance.IsHost;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Quest), nameof(Quest.Create))]
    internal static void OnCreate(Quest __result)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (connection is ElinNetClient) {
            __result.uid = -__result.uid;
            return;
        }

        if (ZoneActivateEvent.IsHappening) {
            return;
        }

        connection.Delta.AddRemote(QuestCreateDelta.Create(__result));
    }
}