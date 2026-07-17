using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestChangePhaseDelta : ElinDelta
{
    [Key(0)]
    public required int Uid { get; init; }

    [Key(1)]
    public required int Modifier { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            // TODO: disable client quest progress
            return;
        }

        var quest = game.quests.list.Find(q => q.uid == Uid);
        quest?.ChangePhase(Modifier);
    }
}