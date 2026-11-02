using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using Newtonsoft.Json;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The one quest log of the world, whoever accepts, advances or completes a quest
/// </summary>
internal static class SharedQuests
{
    private const float HostAnswerTimeout = 10f;

    /// <summary>
    ///     Not part of what a quest is for everyone: its number and phase have their own messages, a deadline is a
    ///     date on one player's clock, the rest is about one player's screen
    /// </summary>
    private static readonly HashSet<string> _own = [
        nameof(Quest.uid),
        nameof(Quest.phase),
        nameof(Quest.deadline),
        nameof(Quest.startDate),
        nameof(Quest.track),
        nameof(Quest.isNew),
    ];

    private static readonly Dictionary<Quest, float> _awaitingHost = [];
    private static readonly Dictionary<Quest, int> _told = [];
    private static readonly Dictionary<Type, FieldInfo[]> _state = [];
    private static Game? _source;

    /// <summary>
    ///     A story quest a dialog started on a client: kept in the quest log, without a number, until the host
    ///     starts it for everyone, so the dialog can go on with it
    /// </summary>
    internal static void AwaitHost(Quest quest)
    {
        _awaitingHost[quest] = Time.unscaledTime + HostAnswerTimeout;
    }

    internal static bool IsAwaitingHost(Quest quest)
    {
        if (!_awaitingHost.TryGetValue(quest, out var until)) {
            return false;
        }

        if (Time.unscaledTime < until) {
            return true;
        }

        // the host did not take it
        _awaitingHost.Remove(quest);
        return false;
    }

    internal static void HostAnswered(string id)
    {
        foreach (var quest in _awaitingHost.Keys.Where(q => q.id == id).ToArray()) {
            _awaitingHost.Remove(quest);
        }
    }

    /// <param name="uid">negative while a quest a dialog started on a client waits for its number from the host</param>
    /// <param name="id">tells which quest it is in that case</param>
    internal static Quest? Find(int uid, string? id)
    {
        var quests = EClass.game.quests;
        return quests.list.Find(q => q.uid == uid) ??
               quests.globalList.Find(q => q.uid == uid) ??
               (uid < 0 && id is not null ? quests.list.Find(q => q.id == id) : null);
    }

    /// <summary>
    ///     What a quest holds besides its phase (who it is for, counters, the stage of the debt) changes all over
    ///     the game: compared with what was last told, like the story flags
    /// </summary>
    internal static void TellChanges()
    {
        if (!EClass.core.IsGameStarted || EClass.game?.quests is not { } quests) {
            return;
        }

        if (!ReferenceEquals(EClass.game, _source)) {
            // another world, or another copy of it: what it holds is what it was given
            _source = EClass.game;
            _told.Clear();
            foreach (var quest in quests.list) {
                Remember(quest);
            }

            return;
        }

        foreach (var gone in _told.Keys.Where(q => !quests.list.Contains(q)).ToArray()) {
            _told.Remove(gone);

            // (a teammate's shared quest: its owner's to tell; one turned in away is told to the host there)
            if (TeamQuests.OnGone(gone)) {
                continue;
            }

            if (PersonalQuests.IsPersonal(gone)) {
                // completed, failed, given up: the host stops keeping it
                PersonalQuests.TellHost(new PersonalQuestDelta {
                    Uid = gone.uid,
                    Data = null,
                });
            }
        }

        foreach (var quest in quests.list.ToArray()) {
            if (quest.uid < 0) {
                // waiting for the host to start it: told once it has its number
                continue;
            }

            // (the karma this player's deeds counted on a quest of the world's log: its share, see SharedQuestKarma)
            var world = !TeamQuests.IsCopy(quest) && !PersonalQuests.IsPersonal(quest);
            if (world) {
                SharedQuestKarma.Collect(quest);
            }

            var hash = LZ4Bytes.ToJson(quest).GetHashCode();
            if (_told.TryGetValue(quest, out var told) && told == hash && !(world && SharedQuestKarma.Untold(quest))) {
                continue;
            }

            _told[quest] = hash;
            var data = LZ4Bytes.Create(quest);

            // a teammate's shared quest this player advanced: the host keeps it for its owner
            if (TeamQuests.TellAdvanced(quest, data)) {
                continue;
            }

            if (PersonalQuests.IsPersonal(quest)) {
                // only its taker holds it, the host keeps it for the next time it joins
                PersonalQuests.TellHost(new PersonalQuestDelta {
                    Uid = quest.uid,
                    Data = data,
                    Now = EClass.world.date.GetRaw(),
                });
                continue;
            }

            var delta = new QuestUpdateDelta {
                Uid = quest.uid,
                Id = quest.id,
                Data = data,
            };
            SharedQuestKarma.Stamp(delta, quest);
            QuestAwaySync.Send(delta);
            NetSession.Instance.Connection?.Delta.AddRemote(delta);
        }
    }

    /// <summary>
    ///     As received or sent: not to be told again
    /// </summary>
    internal static void Remember(Quest quest)
    {
        if (ReferenceEquals(EClass.game, _source)) {
            _told[quest] = LZ4Bytes.ToJson(quest).GetHashCode();
        }
    }

    /// <summary>
    ///     Into the quest everyone here already holds: dialogs and trackers keep pointing at it
    /// </summary>
    internal static void CopyState(Quest from, Quest to)
    {
        if (from.GetType() != to.GetType()) {
            return;
        }

        if (!_state.TryGetValue(to.GetType(), out var fields)) {
            var found = new List<FieldInfo>();
            for (var type = to.GetType(); type is not null && type != typeof(object); type = type.BaseType) {
                found.AddRange(type
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => f.GetCustomAttribute<JsonPropertyAttribute>() is not null && !_own.Contains(f.Name)));
            }

            _state[to.GetType()] = fields = found.ToArray();
        }

        foreach (var field in fields) {
            field.SetValue(to, field.GetValue(from));
        }

        // the task came along with its copy as owner
        to.task?.SetOwner(to);
    }
}
