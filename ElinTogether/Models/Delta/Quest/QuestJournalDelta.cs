using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

/// <summary>
///     The quest log of the world as the host has it, told to every player from time to time. A step of a quest
///     goes to each game as one message (<see cref="QuestChangePhaseDelta" />) and nothing told a game that
///     missed one: its journal stayed late for the rest of the session, the game read the wrong step there (a
///     stair that stays locked, a boss whose death moves nothing) and its player could take the world back
///     (the Vernis quest back to step 3, 2026-10-12). <br />
///     A game only ever catches up: a step it made itself and the host has not heard of yet is never undone
///     (the host answers a refused step by itself), and what a step triggers is not played again here
/// </summary>
[MessagePackObject]
public class QuestJournalDelta : ElinDelta
{
    private const float Period = 20f;
    private static float _next;

    /// <summary>
    ///     uid, phase of each quest in steps of the journal, by pairs
    /// </summary>
    [Key(0)]
    public required int[] Phases { get; init; }

    /// <summary>
    ///     Their ids, in the same order (a quest a dialog started here may not have its number yet)
    /// </summary>
    [Key(1)]
    public required string[] Ids { get; init; }

    [Key(2)]
    public required string[] Completed { get; init; }

    [Key(3)]
    public string[]? CompletedTypes { get; init; }

    /// <summary>
    ///     The story quests on offer (the game's "global" list, not the requests of a town board): a player alone
    ///     on another map is not told of a new offer, and a dialog that hands one out threw without it
    /// </summary>
    [Key(4)]
    public LZ4Bytes? Offers { get; init; }

    /// <summary>
    ///     Tax bills made and paid, the world's count: the tax quest asks "the player" for them, and the bills are
    ///     made in the host's game only (a guest could not talk the quest on)
    /// </summary>
    [Key(5)]
    public long[]? Tax { get; init; }

    /// <summary>
    ///     The world's count of each quest that counts karma (the thieves' guild trial), quest uid -> count: a player
    ///     away loads an older world copy at each move (SharedQuestKarma)
    /// </summary>
    [Key(6)]
    public Dictionary<int, int>? KarmaTotals { get; init; }

    /// <summary>
    ///     And the share of each player's connection already in that count, quest uid -> connection -> share
    /// </summary>
    [Key(7)]
    public Dictionary<int, Dictionary<long, int>>? KarmaShares { get; init; }

    /// <summary>
    ///     Host, end of a frame
    /// </summary>
    internal static void Tell()
    {
        if (NetSession.Instance.Connection is not ElinNetHost { IsZoneSession: false } host || Time.unscaledTime < _next ||
            game?.quests is not { } quests) {
            return;
        }

        _next = Time.unscaledTime + Period;

        // the floors a dungeon opened during this session count too, not only those the world had when it was
        // hosted: the Vernis mine reached and its boss beaten in one evening left the quest at 3 (2026-10-15)
        QuestVernisRepair.Repair();

        // each player or team living its own story: each is told the journal of its own story
        if (TeamOwnStory.CurrentOwner is not null && TeamOwnStory.On) {
            foreach (var group in host.PeerCharas().GroupBy(p => StoryOwner.Of(p.CharaUid))) {
                QuestJournalDelta told;
                using (TeamOwnStory.As(group.Key)) {
                    told = Build(quests);
                }

                foreach (var (peer, _) in group) {
                    host.SendDeltaTo(peer, told);
                }
            }

            return;
        }

        host.Delta.AddRemote(Build(quests));
    }

    private static QuestJournalDelta Build(QuestManager quests)
    {
        var steps = quests.list.Where(q => q is QuestSequence && !PersonalQuests.IsPersonal(q)).ToList();
        SharedQuestKarma.Tell(quests.list.Where(q => !PersonalQuests.IsPersonal(q) && !TeamQuests.IsCopy(q)), out var karma, out var shares);
        return new() {
            KarmaTotals = karma,
            KarmaShares = shares,
            Phases = steps.SelectMany(q => new[] { q.uid, q.phase }).ToArray(),
            Ids = steps.Select(q => q.id).ToArray(),
            Completed = quests.completedIDs.ToArray(),
            CompletedTypes = quests.completedTypes.ToArray(),
            Offers = LZ4Bytes.Create(quests.globalList.Where(q => !q.IsRandomQuest).ToList()),
            Tax = [player.stats.taxBills, player.stats.taxBillsPaid],
        };
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost || game?.quests is not { } quests) {
            return;
        }

        for (var i = 0; i < Ids.Length && i * 2 + 1 < Phases.Length; i++) {
            var id = Ids[i];
            var quest = quests.list.Find(q => q.uid == Phases[i * 2]) ?? quests.list.Find(q => q.id == id);
            var phase = Phases[i * 2 + 1];
            if (quest is not QuestSequence || quest.id != id || quest.phase >= phase || QuestPhaseRepair.IsMissing(quest, phase)) {
                continue;
            }

            EmpLog.Information("Quest {QuestId} was at step {Phase} here, the world is at {World}: caught up", id, quest.phase, phase);
            quest.phase = phase;
            quest.task = null;
            quest.UpdateJournal();
        }

        // finished in the world and still open here
        // (not one the world has open again: a quest finished once and started anew is open in the world)
        foreach (var quest in quests.list.Where(q => q is QuestSequence && !PersonalQuests.IsPersonal(q) && Completed.Contains(q.id) &&
                                                     !Ids.Contains(q.id) && !SharedQuests.IsAwaitingHost(q)).ToList()) {
            EmpLog.Information("Quest {QuestId} was still open here, the world finished it: closed", quest.id);
            QuestCompleteEvent.CompleteQuietly(quest, false);
        }

        // and what this game never heard of at all: the game asks "is that quest done" all over (a dungeon that
        // opens, a resident's talk, the next quest of the story)
        quests.completedIDs.UnionWith(Completed);
        quests.completedTypes.UnionWith(CompletedTypes ?? []);

        foreach (var (uid, total) in KarmaTotals ?? []) {
            if (quests.list.Find(q => q.uid == uid) is { } counting && !PersonalQuests.IsPersonal(counting) && !TeamQuests.IsCopy(counting)) {
                SharedQuestKarma.TakeHost(counting, total, KarmaShares?.GetValueOrDefault(uid));
            }
        }

        if (Tax is { Length: 2 }) {
            player.stats.taxBills = System.Math.Max(player.stats.taxBills, (int)Tax[0]);
            player.stats.taxBillsPaid = System.Math.Max(player.stats.taxBillsPaid, Tax[1]);
        }

        foreach (var offer in Offers?.Decompress<List<Quest>>() ?? []) {
            if (offer is null || quests.completedIDs.Contains(offer.id) || quests.list.Exists(q => q.id == offer.id) ||
                quests.globalList.Exists(q => q.id == offer.id && q.uid == offer.uid)) {
                continue;
            }

            // (the same offer under an older number: the world's replaces it)
            quests.globalList.RemoveAll(q => q.id == offer.id);

            if (offer.person?.chara is not { } chara) {
                EmpLog.Debug("Story quest {QuestId} is on offer in the world, its giver is unknown here: left", offer.id);
                continue;
            }

            EmpLog.Information("Story quest {QuestId} is on offer in the world and was not here: added", offer.id);
            offer.SetClient(chara, false);
            quests.globalList.Add(offer);
        }
    }
}
