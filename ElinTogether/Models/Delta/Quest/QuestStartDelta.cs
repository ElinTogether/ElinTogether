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
    public required bool AssignQuest { get; init; }

    [Key(3)]
    public required LZ4Bytes Data { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        var quest = Data.Decompress<Quest>();

        game.quests.globalList.RemoveAll(q => q.uid == Uid);

        var i = game.quests.list.FindIndex(q => q.uid == Uid);
        if (i >= 0) {
            game.quests.list[i] = quest;
        } else {
            game.quests.list.Insert(0, quest);
        }

        if (Owner?.Find() is Chara owner) {
            quest.SetClient(owner, AssignQuest);
        }

        quest.UpdateJournal();
        if (player.questTracker) {
            WidgetQuestTracker.Show();
        }
    }
}