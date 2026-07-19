using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestStartDelta : ElinDelta
{
    [Key(0)]
    public required int Uid { get; init; }

    [Key(1)]
    public required RemoteCard? Owner { get; init; }

    [Key(2)]
    public required LZ4Bytes? Data { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        var quest = Data?.Decompress<Quest>();
        var i = game.quests.globalList.FindIndex(q => q.uid == Uid);
        if (i >= 0) {
            quest ??= game.quests.globalList[i];
            game.quests.globalList.RemoveAt(i);
        }

        if (quest is null) {
            if (Owner?.Find() is Chara owner && owner.quest.uid == Uid) {
                quest = owner.quest;
            } else {
                return;
            }
        }

        if (game.quests.list.Find(q => q.uid == Uid) is not null) {
            return;
        }

        game.quests.Start(quest);
    }
}