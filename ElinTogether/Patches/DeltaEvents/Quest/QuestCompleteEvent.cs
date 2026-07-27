using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Quest), nameof(Quest.Complete))]
internal static class QuestCompleteEvent
{
    [HarmonyPrefix]
    internal static bool OnClientComplete(Quest __instance)
    {
        if (NetSession.Instance.IsHost) {
            return true;
        }

        var game = EClass.game;
        game.quests.Remove(__instance);
        game.quests.completedIDs.Add(__instance.id);
        game.quests.completedTypes.Add(__instance.GetType().ToString());

        __instance.ShowCompleteText();

        if (__instance.chara?.quest?.uid == __instance.uid) {
            __instance.chara.quest = null;
        }

        __instance.ClientZone?.completedQuests.Add(__instance.uid);
        __instance.isComplete = true;

        return false;
    }

    [HarmonyPostfix]
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