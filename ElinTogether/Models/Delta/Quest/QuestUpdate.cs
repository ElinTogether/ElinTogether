using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     What a quest of the shared log holds, besides its phase, changed somewhere
/// </summary>
[MessagePackObject]
public class QuestUpdateDelta : ElinDelta
{
    internal override bool InSendersStory => true;

    [Key(0)]
    public required LZ4Bytes Data { get; init; }

    [Key(1)]
    public required int Uid { get; init; }

    /// <summary>
    ///     Tells which quest it is while a quest started by a dialog on a client has no number from the host yet
    /// </summary>
    [Key(2)]
    public string? Id { get; init; }

    /// <summary>
    ///     From a player: what its own deeds counted on the quest's karma during this connection (SharedQuestKarma)
    /// </summary>
    [Key(3)]
    public int? Mine { get; set; }

    /// <summary>
    ///     That player's connection, which the host counts its share by
    /// </summary>
    [Key(4)]
    public long Epoch { get; set; }

    /// <summary>
    ///     From the host of the world: its count of the quest's karma
    /// </summary>
    [Key(5)]
    public int? Total { get; set; }

    /// <summary>
    ///     And the share of each connection already in that count
    /// </summary>
    [Key(6)]
    public Dictionary<long, int>? Shares { get; set; }

    protected override void OnApply(ElinNetBase net)
    {
        var quest = SharedQuests.Find(Uid, Id);
        if (quest is null || !game.quests.list.Contains(quest)) {
            return;
        }

        var before = SharedQuestKarma.Before(quest);
        SharedQuests.CopyState(Data.Decompress<Quest>(), quest);
        if (SharedQuestKarma.After(quest, before, this, net)) {
            SharedQuests.Remember(quest);
        }

        if (net is not ElinNetHost host) {
            return;
        }

        var relay = new QuestUpdateDelta {
            Data = Data,
            Uid = quest.uid,
            Id = quest.id,
            Mine = Mine,
            Epoch = Epoch,
        };
        if (!host.IsZoneSession && before is not null) {
            // the host's count, with the sender's share in it
            relay = new() {
                Data = LZ4Bytes.Create(quest),
                Uid = quest.uid,
                Id = quest.id,
            };
            SharedQuestKarma.Stamp(relay, quest);
        }

        host.SendDeltaToAllExcept(OriginPeer, relay);
        // hosting a zone as a client: the host of the world has to know too
        QuestAwaySync.Send(this);
    }
}
