using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Start), typeof(Quest))]
internal class QuestStartEvent
{
    [HarmonyPrefix]
    internal static bool OnClientStart(Quest q, ref Quest __result)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return true;
        }

        __result = q;

        if (ElinDelta.IsApplying) {
            return false;
        }

        if (EClass.game.quests.list.Exists(x => x.uid == q.uid)) {
            return false;
        }

        if (q.uid < 0 || !q.IsRandomQuest || q.UseInstanceZone) {
            EmpPop.Information("emp_ui_quest_client".lang());
            return false;
        }

        client.Delta.AddRemote(new QuestAcceptDelta {
            Uid = q.uid,
            Client = q.person.chara,
        });
        EmpLog.Debug("Requesting quest accept {QuestUid} {QuestId}", q.uid, q.id);

        return false;
    }

    [HarmonyPostfix]
    internal static void OnStart(Quest q)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        if (connection.IsClient) {
            return;
        }

        connection.Delta.AddRemote(new QuestStartDelta {
            Uid = q.uid,
            Owner = q.person.chara,
            AssignQuest = q.chara?.quest?.uid == q.uid,
            Data = LZ4Bytes.Create(q),
        });
    }
}