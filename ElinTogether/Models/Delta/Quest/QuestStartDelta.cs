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
    public required bool IsGlobal { get; init; }

    [Key(3)]
    public required LZ4Bytes? Data { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        var quest = Data?.Decompress<Quest>();
        if (quest is null) {
            if (IsGlobal) {
                quest = game.quests.globalList.Find(q => q.uid == Uid);
            } else if (Owner?.Find() is Chara owner && owner.quest.uid == Uid) {
                quest = owner.quest;
            } else {
                return;
            }

            if (game.quests.list.Contains(quest)) {
                return;
            }
        }

        var i = game.quests.globalList.FindIndex(q => q.uid == Uid);
        game.quests.globalList.RemoveAt(i);

        game.quests.Start(quest);
    }
}