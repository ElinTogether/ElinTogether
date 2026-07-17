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
        if (Data?.Decompress<Quest>() is { } quest) {
            game.quests.Start(quest);
            return;
        }

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

        game.quests.Start(quest);
    }
}