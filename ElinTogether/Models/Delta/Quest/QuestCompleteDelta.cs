using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestCompleteDelta : ElinDelta
{
    [Key(1)]
    public required int Uid { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        var quest = game.quests.list.Find(q => q.uid == Uid) ??
                    game.quests.globalList.Find(q => q.uid == Uid);
        if (quest is null || quest.isComplete) {
            return;
        }

        if (net.IsHost) {
            using (Simulate()) {
                quest.Complete();
            }

            return;
        }

        quest.Complete();
    }
}