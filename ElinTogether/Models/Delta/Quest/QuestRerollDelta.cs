using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Client -> the game that keeps the map: the "Reroll Quests" button of the quest board. That game draws the
///     offers again, as its own button does, and pays the influence in its copy as the client did in its own;
///     the new offers and the ones gone reach every game (<see cref="QuestCreateDelta" />, <see cref="QuestOffersDelta" />).
///     <br />
///     Also what a player's arrival or look at the board does for free in the game of the one who keeps the map
///     (<see cref="Kind" />): the day's new offers (Zone.UpdateQuests, gated by the zone's dateQuest), and on
///     arrival the town coming back to life (Zone.OnVisit: the dead townspeople revived, the merchants restocked,
///     gated by dateRevive). A guest on the host's map never got either: only the host's own arrival made them
/// </summary>
[MessagePackObject]
public class QuestRerollDelta : ElinDelta
{
    // LayerQuestBoard, GetCost
    private const int Cost = 1;

    internal const int Reroll = 0;
    internal const int Board = 1;
    internal const int Visit = 2;

    [Key(0)]
    public required int ZoneUid { get; init; }

    /// <summary>
    ///     <see cref="Reroll" /> (the button, paid), <see cref="Board" /> (the board opened), <see cref="Visit" />
    ///     (the player arrived on the map)
    /// </summary>
    [Key(1)]
    public int Kind { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost || _zone is not { } zone || zone.uid != ZoneUid || zone.map is null) {
            EmpLog.Debug("Quest draw {Kind} for zone {ZoneUid} from peer {Peer} dropped: host on zone {Here}", Kind, ZoneUid,
                OriginPeer, _zone?.uid);
            return;
        }

        EmpLog.Debug("Quest draw {Kind} for zone {ZoneUid} from peer {Peer}: quests due {Due}, revive due {Revive}", Kind, ZoneUid,
            OriginPeer, zone.dateQuest <= world.date.GetRaw(), world.date.IsExpired(zone.dateRevive));

        switch (Kind) {
            case Reroll: {
                if (zone.influence < Cost) {
                    return;
                }

                zone.influence -= Cost;

                // as this game's own gesture, so that what it draws is told like any draw made here
                using var _ = Simulate();
                zone.UpdateQuests(true);
                return;
            }
            case Board: {
                using var _ = Simulate();
                zone.UpdateQuests();
                return;
            }
            case Visit: {
                using var _ = Simulate();
                Revive(zone);
                zone.UpdateQuests();
                return;
            }
        }
    }

    /// <summary>
    ///     What Zone.OnVisit does to a town (or a base) whose people are due back, as the host's own arrival does
    /// </summary>
    private static void Revive(Zone zone)
    {
        if (zone is not Zone_Civilized && !zone.IsPCFaction || !world.date.IsExpired(zone.dateRevive)) {
            return;
        }

        zone.ResetHostility();
        zone.Revive();
        List<Chara> minions = [];
        foreach (var chara in _map.charas) {
            chara.TryRestock(false);
            if (!chara.IsPCFaction) {
                chara.c_fur = 0;
            }

            // (a summon of another player, or of its party, is that player's: not a stray one)
            if (chara.IsMinion && chara.master is { IsPCParty: false } master && !PlayerRevive.IsPlayerChara(master) &&
                !(master.party?.leader is { } leader && PlayerRevive.IsPlayerChara(leader))) {
                minions.Add(chara);
            }
        }

        foreach (var minion in minions) {
            minion.Destroy();
        }
    }
}
