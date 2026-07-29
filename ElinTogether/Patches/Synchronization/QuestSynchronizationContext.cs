using ElinTogether.Helper;

namespace ElinTogether.Patches;

internal class QuestSynchronizationContext : SynchronizationContext
{
    internal static void Update()
    {
        game.quests.list.RemoveAll(q => q.uid < 0);
        game.quests.globalList.RemoveAll(q => q.uid < 0);

        foreach (var chara in _map.charas) {
            if (chara.quest is not null && chara.IsPlayer) {
                EmpLog.Warning("Removing quest from player chara {Uid} {QuestUid} {QuestId}",
                    chara.uid, chara.quest.uid, chara.quest.id);
                chara.quest = null;
            }
        }
    }
}