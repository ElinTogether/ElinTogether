using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     Host box "Teammates share quests" (user 2026-10-30), on top of teams and personal quests: a player in a team
///     ticks "Share my quests with my team" on the Team page. Its quests (the ones it holds while the box is ticked)
///     are then, for its teammates, what the world's quests are for everyone (<see cref="SharedQuests" />), scoped to
///     the team: in their journal, advanced and turned in by any of them, the reward to every teammate in the game. <br />
///     The host keeps who shares and which quests are shared (<see cref="ElinNetHost.QuestSharers" />), and sends each
///     player the shared quests of its team (<see cref="TeamQuestListDelta" />). This side holds the copies: a
///     teammate's quest in the local journal, kept apart from the player's own personal quests
/// </summary>
[HarmonyPatch]
internal static class TeamQuests
{
    // a guest's copy of who shares
    private static HashSet<int> _sharers = [];

    // quest uid -> its owner and the copy this game shows
    private static readonly Dictionary<int, (int Owner, Quest Quest)> _copies = [];

    // quests closed here or by the host: a list already on its way does not bring them back
    private static readonly HashSet<int> _closed = [];

    private static object? _transport;

    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules. Teams and personal
    ///     quests must be on too
    /// </summary>
    internal static bool On => NetSession.Instance.Transport is not null && NetSession.Instance.Rules is { UseTeams: true, UsePersonalQuests: true } rules &&
                               (KeepsBook ? EmpConfig.Server.TeamQuestSharing.Value : rules.TeamQuestSharing);

    internal static bool Shares(int charaUid)
    {
        return KeepsBook ? ElinNetHost.QuestSharers.ContainsKey(charaUid) : _sharers.Contains(charaUid);
    }

    /// <summary>
    ///     What every game is told with the teams: none while the box is off
    /// </summary>
    internal static int[]? Told()
    {
        return On ? ElinNetHost.QuestSharers.Keys.ToArray() : null;
    }

    internal static void Learn(int[]? sharers)
    {
        _sharers = [..sharers ?? []];
    }

    /// <summary>
    ///     The local player's box on the Team page, asked of the game that keeps the book
    /// </summary>
    internal static void Ask(bool on)
    {
        Teams.Ask(TeamAsk.ShareQuests, on ? 1 : 0);
    }

    /// <summary>
    ///     In the game that keeps the book: that player shares its quests or stops. Stopping leaves the quests already
    ///     shared shared until they are done; only a player in a team starts sharing
    /// </summary>
    internal static bool SetSharing(Chara chara, bool on)
    {
        // (starting needs a team whose rank shares quests, TeamRanks; stopping is always one's own choice)
        if (!KeepsBook || !On || (on && !TeamRanks.Can(chara.uid, TeamPerm.Quests)) || Shares(chara.uid) == on) {
            return false;
        }

        if (on) {
            ElinNetHost.QuestSharers[chara.uid] = 1;
        } else {
            ElinNetHost.QuestSharers.Remove(chara.uid);
        }

        EmpLog.Information("Player {CharaUid} {Shares} its quests with its team", chara.uid, on ? "shares" : "stops sharing");
        // (the quests it already holds are shared at once)
        (NetSession.Instance.Transport as ElinNetHost)?.TickTeamQuests(true);
        return true;
    }

    internal static bool IsCopy(Quest quest)
    {
        return _copies.TryGetValue(quest.uid, out var copy) && ReferenceEquals(copy.Quest, quest);
    }

    internal static bool IsCopyUid(int questUid)
    {
        return _copies.ContainsKey(questUid);
    }

    [ElinPreLoad]
    private static void Forget(GameIOContext context)
    {
        // (a guest's world is replaced at every move: its copies go on in the next one, see Tick)
        if (NetSession.Instance.Transport is ElinNetClient) {
            return;
        }

        _copies.Clear();
        _closed.Clear();
        _sharers = [];
    }

    /// <summary>
    ///     Every half second: the copies are back in a world just loaded (a guest's world is replaced at every move,
    ///     and PersonalQuests.Restore keeps only the player's own), gone when the box or the connection is
    /// </summary>
    internal static void Tick()
    {
        var transport = NetSession.Instance.Transport;
        if (!ReferenceEquals(transport, _transport)) {
            // another connection: what it showed belongs to the world of the last one
            _transport = transport;
            _copies.Clear();
            _closed.Clear();
            _sharers = [];
        }

        if (!EClass.core.IsGameStarted || EClass.game?.quests is not { } quests) {
            return;
        }

        if (!On) {
            DropAll();
            return;
        }

        foreach (var (_, quest) in _copies.Values.ToArray()) {
            if (!quests.list.Contains(quest)) {
                Place(quest);
            }
        }
    }

    private static void DropAll()
    {
        foreach (var uid in _copies.Keys.ToArray()) {
            Drop(uid);
        }
    }

    /// <summary>
    ///     From the host: the shared quests of this player's team that changed (its own included, when a teammate
    ///     advanced it), the ones it no longer sees (it left the team), the ones that are done
    /// </summary>
    /// <param name="now">the date on the host's clock: deadlines are dates on it</param>
    internal static void Receive(IReadOnlyList<(int Owner, Quest Quest)> quests, int[]? removed, int[]? closed, int now)
    {
        if (EClass.game?.quests is not { } log || EClass.pc is not { } pc) {
            return;
        }

        foreach (var uid in closed ?? []) {
            Close(uid);
        }

        foreach (var uid in removed ?? []) {
            Drop(uid);
        }

        var shift = now > 0 ? EClass.world.date.GetRaw() - now : 0;
        foreach (var (owner, quest) in quests) {
            if (_closed.Contains(quest.uid)) {
                continue;
            }

            if (quest.deadline > 0) {
                quest.deadline += shift;
            }

            if (_copies.TryGetValue(quest.uid, out var copy)) {
                Into(quest, copy.Quest);
                _copies[quest.uid] = (owner, copy.Quest);
                continue;
            }

            var held = log.list.Find(q => q.uid == quest.uid);
            if (owner == pc.uid) {
                // the player's own, advanced by a teammate
                if (held is not null) {
                    Into(quest, held);
                }

                continue;
            }

            if (held is not null) {
                continue;
            }

            _copies[quest.uid] = (owner, quest);
            Place(quest);
            EmpLog.Debug("Quest {QuestUid} {QuestId} of teammate {Owner} shows in this journal", quest.uid, quest.id, owner);
        }

        if (EClass.player?.questTracker == true) {
            WidgetQuestTracker.Show();
        }
    }

    private static void Into(Quest from, Quest to)
    {
        SharedQuests.CopyState(from, to);
        to.phase = from.phase;
        to.deadline = from.deadline;
        SharedQuests.Remember(to);
    }

    /// <summary>
    ///     In the journal, its giver offering to hear of it, as PersonalQuests.Restore puts back the player's own
    /// </summary>
    private static void Place(Quest quest)
    {
        var quests = EClass.game.quests;
        if (!quests.list.Contains(quest)) {
            quests.list.Add(quest);
        }

        if (quest.person is { } person) {
            person._tempChara = null;
            person.refChara = new();
            var giver = EClass.game.activeZone?.map is null
                ? EClass.game.cards.globalCharas.TryGetValue(person.uidChara)
                : quest.chara;
            // (not over an offer the giver has now)
            if (giver is not null && (giver.quest is null || giver.quest.uid == quest.uid)) {
                giver.quest = quest;
            }
        }

        SharedQuests.Remember(quest);
    }

    /// <summary>
    ///     Out of this journal without a word: not this player's to see anymore
    /// </summary>
    private static void Drop(int uid)
    {
        if (!_copies.TryGetValue(uid, out var copy)) {
            return;
        }

        _copies.Remove(uid);
        if (EClass.game?.quests is { } quests) {
            quests.list.Remove(copy.Quest);
        }

        if (copy.Quest.chara?.quest == copy.Quest) {
            copy.Quest.chara.quest = null;
        }
    }

    /// <summary>
    ///     Done by a teammate: done here too, without the rewards (the host gave this player its share)
    /// </summary>
    private static void Close(int uid)
    {
        _closed.Add(uid);
        var quest = _copies.TryGetValue(uid, out var copy) ? copy.Quest : EClass.game.quests.list.Find(q => q.uid == uid && PersonalQuests.IsPersonal(q));
        _copies.Remove(uid);
        if (quest is not null && EClass.game.quests.list.Contains(quest)) {
            QuestCompleteEvent.CompleteQuietly(quest, false);
        }
    }

    /// <summary>
    ///     SharedQuests.TellChanges found a copy changed: the player advanced a teammate's quest. False: not a copy
    /// </summary>
    internal static bool TellAdvanced(Quest quest, LZ4Bytes data)
    {
        if (!IsCopy(quest)) {
            return false;
        }

        var now = EClass.world.date.GetRaw();
        if (NetSession.Instance.Transport is ElinNetHost host) {
            host.UpdateTeamQuest(EClass.pc.uid, quest.uid, data, now);
        } else {
            PersonalQuests.TellHost(new TeamQuestUpdateDelta {
                Uid = quest.uid,
                Data = data,
                Now = now,
            });
        }

        return true;
    }

    /// <summary>
    ///     SharedQuests.TellChanges found a quest gone from the journal. A player away from the host (travelling alone,
    ///     a guest on another player's map) turned it in there and had its reward there: the host is told, and gives
    ///     the teammates their share. On the host's map the host ran the turn-in itself (QuestCompleteDelta). True:
    ///     a copy, nothing else to tell
    /// </summary>
    internal static bool OnGone(Quest quest)
    {
        var copy = IsCopy(quest);
        if (!copy && !PersonalQuests.IsPersonal(quest)) {
            return false;
        }

        if (copy && !quest.isComplete) {
            // out of a world replaced, or failed with the owner's: Tick or the host says
            return true;
        }

        if (copy) {
            _copies.Remove(quest.uid);
        }

        if (On && quest.isComplete && _closed.Add(quest.uid) && NetSession.Instance is { IsAway: true, Transport: ElinNetClient }) {
            PersonalQuests.TellHost(new TeamQuestDoneDelta {
                Uid = quest.uid,
                Data = LZ4Bytes.Create(quest),
            });
        }

        return copy;
    }

    // ---- what the game counts

    /// <summary>
    ///     A teammate's quest does not take one of the player's five places
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.CountRandomQuest))]
    private static void OnCountRandomQuest(QuestManager __instance, ref int __result)
    {
        if (_copies.Count == 0) {
            return;
        }

        foreach (var (_, quest) in _copies.Values) {
            if (quest is QuestRandom && __instance.list.Contains(quest)) {
                __result--;
            }
        }
    }

    // the copies taken out of the journal while the game writes its save: they are the owner's, not this world's
    private static readonly List<Quest> _unsaved = [];

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void BeforeSave()
    {
        _unsaved.Clear();
        if (_copies.Count == 0 || EClass.game?.quests is not { } quests) {
            return;
        }

        foreach (var (_, quest) in _copies.Values) {
            if (quests.list.Remove(quest)) {
                _unsaved.Add(quest);
            }
        }
    }

    // a finalizer: a save that throws (in the game or in another mod's prefix) does not lose the copies
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void AfterSave()
    {
        if (EClass.game?.quests is { } quests) {
            foreach (var quest in _unsaved.Where(q => !quests.list.Contains(q))) {
                quests.list.Add(quest);
            }
        }

        _unsaved.Clear();
    }
}
