using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class QuestStartEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Start), typeof(Quest))]
    internal static void OnStart(Quest q)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        // already started
        var quests = EClass.game.quests;
        if (quests.list.Contains(q)) {
            return;
        }

        var owner = q.person.chara;
        var canFind = ReferenceEquals(owner?.quest, q);
        var isGlobal = quests.globalList.Contains(q);
        connection.Delta.AddRemote(new QuestStartDelta {
            Uid = q.uid,
            Owner = owner,
            IsGlobal = isGlobal,
            Data = (!canFind && !isGlobal) ? LZ4Bytes.Create(q) : null,
        });
    }
}