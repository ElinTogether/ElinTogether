using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestUpdateDelta : ElinDelta
{
    [Key(0)]
    public required LZ4Bytes Data { get; init; }

    [Key(1)]
    public required bool AssignQuest { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (NetSession.Instance.IsHost) {
            return;
        }

        var quest = Data.Decompress<Quest>();

        var i = game.quests.list.FindIndex(q => q.uid == quest.uid);
        game.quests.list[i] = quest;
        if (quest.person.chara is not { } chara) {
            return;
        }

        quest.SetClient(chara, AssignQuest);
    }

    public static QuestUpdateDelta Create(Quest quest)
    {
        return new() {
            Data = LZ4Bytes.Create(quest),
            AssignQuest = quest.person.chara?.quest == quest,
        };
    }
}