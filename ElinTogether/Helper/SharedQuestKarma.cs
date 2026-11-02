using System;
using System.Collections.Generic;
using ElinTogether.Models;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     Issue #63: the karma a quest of the shared log counts (the thieves' guild trial, QuestTaskKarma) is counted
///     once, in the game of the player whose deed cost it (<see cref="PlayerKarma" />). The quest travels as a whole
///     copy (<see cref="SharedQuests" />) and the last copy told won: a copy told by another game a moment later, or
///     the world copy a player away loads at each move, put an older count back, and the player's karma never moved
///     the quest. <br />
///     Each player now also tells what its own deeds counted during this connection, as a running total (a copy told
///     twice, late or replayed after a load counts once). The host of the world adds each player's share to its count
///     once, and tells its count with the shares it holds. A game takes the host's count plus what of its own the
///     host has not heard yet: no copy, old or new, takes a player's progress away. <br />
///     Only karma: a kill or a gift plays in every game that sees it (the death is played everywhere), each game
///     counts it, and there the whole copy is the right merge
/// </summary>
internal static class SharedQuestKarma
{
    // quest uid -> the count as last looked at in this game; what moves it in between is this player's deeds
    private static readonly Dictionary<int, (Game Game, QuestTaskKarma Task, int Num)> _seen = [];

    // quest uid -> what this player's deeds counted during this connection, and how much of it was told
    private static readonly Dictionary<int, int> _mine = [];
    private static readonly Dictionary<int, int> _told = [];

    // host of the world: quest uid -> connection of a player -> its share already in the count
    private static readonly Dictionary<int, Dictionary<long, int>> _shares = [];

    private static object? _transport;
    private static long _epoch;

    private static bool IsWorldHost => NetSession.Instance.Transport is ElinNetHost;

    private static int? Num(Quest quest)
    {
        return quest.task is QuestTaskKarma task ? task.num : null;
    }

    /// <summary>
    ///     A new connection: the host counts from what it holds, every share starts again
    /// </summary>
    private static void Sync()
    {
        var transport = NetSession.Instance.Transport;
        if (ReferenceEquals(transport, _transport)) {
            return;
        }

        _transport = transport;
        _seen.Clear();
        _mine.Clear();
        _told.Clear();
        _shares.Clear();
        _epoch = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
    }

    /// <summary>
    ///     What this player's deeds counted since the last look, into its share. A world loaded since, or a task put
    ///     in by a copy, is a new start, not a deed
    /// </summary>
    internal static void Collect(Quest quest)
    {
        Sync();
        if (quest.task is not QuestTaskKarma task) {
            _seen.Remove(quest.uid);
            _mine.Remove(quest.uid);
            _told.Remove(quest.uid);
            return;
        }

        if (_seen.TryGetValue(quest.uid, out var seen) && ReferenceEquals(seen.Game, EClass.game) && ReferenceEquals(seen.Task, task)) {
            _mine[quest.uid] = _mine.GetValueOrDefault(quest.uid) + task.num - seen.Num;
        }

        _seen[quest.uid] = (EClass.game, task, task.num);
    }

    /// <summary>
    ///     The count was set from elsewhere (a copy, the host's count): not a deed
    /// </summary>
    private static void Rebase(Quest quest)
    {
        if (quest.task is QuestTaskKarma task) {
            _seen[quest.uid] = (EClass.game, task, task.num);
        } else {
            _seen.Remove(quest.uid);
        }
    }

    /// <summary>
    ///     This player has a share the host has not been told of yet
    /// </summary>
    internal static bool Untold(Quest quest)
    {
        // (the host's deeds are in its count)
        return !IsWorldHost && _mine.GetValueOrDefault(quest.uid) != _told.GetValueOrDefault(quest.uid);
    }

    /// <summary>
    ///     What goes with a copy told from here: the host its count and the shares in it, a player its own share
    /// </summary>
    internal static void Stamp(QuestUpdateDelta delta, Quest quest)
    {
        Sync();
        if (Num(quest) is not { } num) {
            return;
        }

        if (IsWorldHost) {
            delta.Total = num;
            delta.Shares = _shares.TryGetValue(quest.uid, out var shares) ? new(shares) : [];
            return;
        }

        var mine = _mine.GetValueOrDefault(quest.uid);
        delta.Mine = mine;
        delta.Epoch = _epoch;
        _told[quest.uid] = mine;
    }

    /// <summary>
    ///     Before a copy goes in: this game's own count
    /// </summary>
    internal static int? Before(Quest quest)
    {
        Collect(quest);
        return Num(quest);
    }

    /// <summary>
    ///     After a copy went in (it brought its sender's count): the count this game keeps
    /// </summary>
    /// <returns>false while this player has a share left to tell: the copy is not to be taken as told</returns>
    internal static bool After(Quest quest, int? before, QuestUpdateDelta delta, ElinNetBase net)
    {
        if (before is { } kept && quest.task is QuestTaskKarma task) {
            if (net is ElinNetHost { IsZoneSession: false }) {
                if (delta.Mine is { } mine) {
                    // the sender's share, once, onto this count
                    if (!_shares.TryGetValue(quest.uid, out var shares)) {
                        _shares[quest.uid] = shares = [];
                    }

                    task.num = kept + mine - shares.GetValueOrDefault(delta.Epoch);
                    shares[delta.Epoch] = mine;
                }

                // (a game of an older version tells its whole count: the last one told wins, as before)
            } else if (delta.Total is { } total) {
                TakeHost(task, total, delta.Shares, quest.uid);
            } else if (delta.Mine is not null) {
                // another player's game, not the host: its share comes here with the host's count
                task.num = kept;
            }
        }

        Rebase(quest);
        return IsWorldHost || !Untold(quest);
    }

    /// <summary>
    ///     The host's count, told with the journal: a world copy loaded at a move held an older one
    /// </summary>
    internal static void TakeHost(Quest quest, int total, Dictionary<long, int>? shares)
    {
        if (IsWorldHost || quest.task is not QuestTaskKarma task) {
            return;
        }

        Collect(quest);
        TakeHost(task, total, shares, quest.uid);
        Rebase(quest);
    }

    private static void TakeHost(QuestTaskKarma task, int total, Dictionary<long, int>? shares, int questUid)
    {
        // what of this player's own the host has not counted yet comes on top
        task.num = total + _mine.GetValueOrDefault(questUid) - (shares?.GetValueOrDefault(_epoch) ?? 0);
    }

    /// <summary>
    ///     Host of the world, with the journal: the count of each quest of the shared log that counts karma, and the
    ///     shares in it
    /// </summary>
    internal static void Tell(IEnumerable<Quest> quests, out Dictionary<int, int>? totals, out Dictionary<int, Dictionary<long, int>>? shares)
    {
        Sync();
        totals = null;
        shares = null;
        foreach (var quest in quests) {
            if (Num(quest) is not { } num) {
                continue;
            }

            (totals ??= [])[quest.uid] = num;
            (shares ??= [])[quest.uid] = _shares.TryGetValue(quest.uid, out var held) ? new(held) : [];
        }
    }
}
