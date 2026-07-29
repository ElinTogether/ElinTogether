using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestAcceptDelta : ElinDelta
{
    [Key(0)]
    public required int Uid { get; init; }

    [Key(1)]
    public required RemoteCard? Client { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (!net.IsHost) {
            return;
        }

        var quest = ResolveQuest();
        if (quest is null) {
            EmpLog.Warning("Rejecting quest accept, unresolved quest {QuestUid}", Uid);
            return;
        }

        if (game.quests.list.Exists(q => q.uid == Uid)) {
            return;
        }

        if (!quest.IsRandomQuest || quest.UseInstanceZone) {
            EmpLog.Warning("Rejecting quest accept, not client acceptable {QuestUid} {QuestId}", Uid, quest.id);
            return;
        }

        if (game.quests.CountRandomQuest() >= QuestManager.MaxRandomQuest) {
            EmpLog.Debug("Rejecting quest accept, quest list full {QuestUid}", Uid);
            return;
        }

        using var _ = Simulate();
        game.quests.Start(quest);
        EmpLog.Debug("Accepted client quest {QuestUid} {QuestId} for peer {PeerIndex}", Uid, quest.id, OriginPeer);
    }

    private Quest? ResolveQuest()
    {
        if (Client?.Find() is Chara owner && owner.quest?.uid == Uid) {
            return owner.quest;
        }

        return game.quests.globalList.Find(q => q.uid == Uid);
    }
}