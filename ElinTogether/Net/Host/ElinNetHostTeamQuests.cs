using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Patches;

namespace ElinTogether.Net;

/// <summary>
///     Host box "Teammates share quests" (<see cref="TeamQuests" />): who shares, which quests are shared, what each
///     player's game was last sent of them. A shared quest is read where the host keeps it for its owner: the host's
///     own quest log for the host's quests, the personal log (<see cref="PersonalQuestLogs" />) for a guest's
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Player chara uid -> 1: that player shares its quests with its team (its box on the Team page)
    /// </summary>
    [ElinGameIOProperty("team_quest_sharers")]
    internal static Dictionary<int, int> QuestSharers
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Quest uid -> its owner's chara uid: the quests shared with the owner's team, until they are done
    /// </summary>
    [ElinGameIOProperty("team_shared_quests")]
    private static Dictionary<int, int> TeamSharedQuests
    {
        get => field ??= [];
        set;
    }

    // (player chara uid, quest uid) -> the hash of that quest as that player's game last had it
    private readonly Dictionary<(int Chara, int Quest), int> _teamQuestKnown = [];

    // player chara uid -> the shared quests its game was sent and still shows
    private readonly Dictionary<int, HashSet<int>> _teamQuestShown = [];

    // the owner's own quests a teammate advanced last: the owner's game is sent them
    private readonly HashSet<int> _teamQuestFromMate = [];

    // quests that are never shared (their zone is the taker's own)
    private readonly HashSet<int> _teamQuestUnshareable = [];

    private float _teamQuestNext;

    /// <summary>
    ///     Once a second (at once when asked): the quests the sharers hold are shared, and each player's game is sent
    ///     what changed in the shared quests of its team
    /// </summary>
    internal void TickTeamQuests(bool now = false)
    {
        if (IsZoneSession || !core.IsGameStarted || game?.quests is null || world?.date is null) {
            return;
        }

        if (!now && UnityEngine.Time.unscaledTime < _teamQuestNext) {
            return;
        }

        _teamQuestNext = UnityEngine.Time.unscaledTime + 1f;

        if (!TeamQuests.On) {
            // the box went off: every copy leaves the journals (the quests stay their owners')
            foreach (var (peer, uid) in TeamQuestPlayers()) {
                if (_teamQuestShown.TryGetValue(uid, out var shown) && shown.Count > 0) {
                    SendTeamQuests(peer, new() { Quests = [], Owners = [], Removed = shown.ToArray() });
                }
            }

            _teamQuestShown.Clear();
            _teamQuestKnown.Clear();
            return;
        }

        MarkSharedQuests();

        // quest uid -> owner, what it holds, its hash
        var shared = new Dictionary<int, (int Owner, byte[] Bytes, int Hash)>();
        foreach (var (uid, owner) in TeamSharedQuests.ToArray()) {
            if (SharedQuestBytes(owner, uid) is { } bytes) {
                shared[uid] = (owner, bytes, Hash(bytes));
            } else {
                // failed, given up, or gone with a world: not shared anymore
                TeamSharedQuests.Remove(uid);
                _teamQuestFromMate.Remove(uid);
            }
        }

        foreach (var (peer, uid) in TeamQuestPlayers()) {
            PushTeamQuests(peer, uid, shared);
        }
    }

    /// <summary>
    ///     Every player in the game: the host's own (peer -1) and the connected ones, wherever they are
    /// </summary>
    private IEnumerable<(int Peer, int CharaUid)> TeamQuestPlayers()
    {
        if (!ServerAvatar.Is(pc)) {
            yield return (-1, pc.uid);
        }

        foreach (var pair in PeerCharas()) {
            yield return pair;
        }
    }

    private void SendTeamQuests(int peer, TeamQuestListDelta delta)
    {
        delta.Now = world.date.GetRaw();
        if (peer < 0) {
            TeamQuests.Receive(delta.Read(), delta.Removed, delta.Closed, delta.Now);
        } else {
            SendDeltaTo(peer, delta);
        }
    }

    /// <summary>
    ///     What a player who shares holds now is shared with its team: taken while the box is ticked, or held when it
    ///     ticked it. A player who unticks it keeps the ones already shared shared, until they are done
    /// </summary>
    private void MarkSharedQuests()
    {
        foreach (var sharer in QuestSharers.Keys.ToArray()) {
            if (Teams.TeamOf(sharer) == Teams.Wanderers) {
                continue;
            }

            foreach (var (uid, read) in HeldPersonal(sharer)) {
                if (TeamSharedQuests.ContainsKey(uid) || _teamQuestUnshareable.Contains(uid)) {
                    continue;
                }

                // (the zone of such a quest is its taker's alone: it stays the taker's)
                if (read() is not { UseInstanceZone: false } quest) {
                    _teamQuestUnshareable.Add(uid);
                    continue;
                }

                TeamSharedQuests[uid] = sharer;
                EmpLog.Debug("Quest {QuestUid} {QuestId} of player {CharaUid} is shared with its team", uid, quest.id, sharer);
            }
        }
    }

    /// <summary>
    ///     The personal quests the host keeps for that player: the host's own in its quest log, a guest's in its log
    /// </summary>
    private IEnumerable<(int Uid, Func<Quest?> Read)> HeldPersonal(int owner)
    {
        if (owner == pc.uid) {
            foreach (var quest in game.quests.list.Where(q => PersonalQuests.IsPersonal(q) && !TeamQuests.IsCopy(q)).ToArray()) {
                yield return (quest.uid, () => quest);
            }

            yield break;
        }

        if (!PersonalQuestLogs.TryGetValue(owner, out var log)) {
            yield break;
        }

        foreach (var (uid, bytes) in log.ToArray()) {
            yield return (uid, () => new LZ4Bytes { Bytes = bytes }.Decompress<Quest>());
        }
    }

    private byte[]? SharedQuestBytes(int owner, int uid)
    {
        if (owner == pc.uid) {
            return game.quests.list.Find(q => q.uid == uid && !TeamQuests.IsCopy(q)) is { } own ? LZ4Bytes.Create(own).Bytes : null;
        }

        return PersonalQuestLogs.TryGetValue(owner, out var log) && log.TryGetValue(uid, out var bytes) ? bytes : null;
    }

    private static int Hash(byte[] bytes)
    {
        unchecked {
            var hash = (int)2166136261;
            foreach (var b in bytes) {
                hash = (hash ^ b) * 16777619;
            }

            return hash ^ bytes.Length;
        }
    }

    /// <summary>
    ///     One player's game: the shared quests of its team it does not have as they are, and the ones it should no
    ///     longer show. Its own only when a teammate advanced them
    /// </summary>
    private void PushTeamQuests(int peer, int charaUid, Dictionary<int, (int Owner, byte[] Bytes, int Hash)> shared)
    {
        var team = Teams.TeamOf(charaUid);
        if (!_teamQuestShown.TryGetValue(charaUid, out var shown)) {
            shown = _teamQuestShown[charaUid] = [];
        }

        var quests = new List<LZ4Bytes>();
        var owners = new List<int>();
        var visible = new HashSet<int>();
        foreach (var (uid, (owner, bytes, hash)) in shared) {
            if (team == Teams.Wanderers || Teams.TeamOf(owner) != team) {
                continue;
            }

            visible.Add(uid);
            var key = (charaUid, uid);
            if (_teamQuestKnown.TryGetValue(key, out var known) && known == hash) {
                continue;
            }

            _teamQuestKnown[key] = hash;
            // (its own as its game has it, unless a teammate moved it on; the host's own is moved on in place)
            if (owner == charaUid && (peer < 0 || !_teamQuestFromMate.Contains(uid))) {
                continue;
            }

            quests.Add(new() { Bytes = bytes });
            owners.Add(owner);
        }

        var removed = shown.Where(uid => !visible.Contains(uid)).ToArray();
        foreach (var uid in removed) {
            _teamQuestKnown.Remove((charaUid, uid));
        }

        shown.Clear();
        shown.UnionWith(visible);

        if (quests.Count == 0 && removed.Length == 0) {
            return;
        }

        SendTeamQuests(peer, new() { Quests = quests, Owners = owners.ToArray(), Removed = removed });
    }

    /// <summary>
    ///     A player settled on this map (joining, coming back): its game holds only what it was just given, it is sent
    ///     the shared quests of its team again
    /// </summary>
    private void ForgetTeamQuestsOf(int charaUid)
    {
        _teamQuestShown.Remove(charaUid);
        foreach (var key in _teamQuestKnown.Keys.Where(k => k.Chara == charaUid).ToArray()) {
            _teamQuestKnown.Remove(key);
        }
    }

    /// <summary>
    ///     A teammate (not the owner) may see, advance and turn in that quest: it is shared, and both are in one team
    /// </summary>
    private static bool MayUseTeamQuest(int charaUid, int questUid, out int owner)
    {
        owner = 0;
        if (!TeamQuests.On || !TeamSharedQuests.TryGetValue(questUid, out owner) || owner == charaUid) {
            return false;
        }

        var team = Teams.TeamOf(charaUid);
        return team != Teams.Wanderers && Teams.TeamOf(owner) == team;
    }

    /// <summary>
    ///     A teammate advanced a shared quest in its game: kept for its owner (its reward and kind stay as the owner
    ///     took it), sent to the others
    /// </summary>
    internal void UpdateTeamQuest(int charaUid, int questUid, LZ4Bytes data, int now)
    {
        if (IsZoneSession || !MayUseTeamQuest(charaUid, questUid, out var owner)) {
            EmpLog.Debug("Player {CharaUid} advances quest {QuestUid}, not a shared quest of its team", charaUid, questUid);
            return;
        }

        Quest? quest;
        try {
            quest = data.Decompress<Quest>();
        } catch (Exception ex) {
            EmpLog.Information(ex, "Shared quest {QuestUid} from player {CharaUid} could not be read", questUid, charaUid);
            return;
        }

        if (quest is null || quest.uid != questUid) {
            return;
        }

        var shift = now > 0 ? world.date.GetRaw() - now : 0;
        if (quest.deadline > 0) {
            quest.deadline += shift;
        }

        if (owner == pc.uid) {
            if (game.quests.list.Find(q => q.uid == questUid && !TeamQuests.IsCopy(q)) is not { } own || !KeepTerms(own, quest)) {
                return;
            }

            SharedQuests.CopyState(quest, own);
            own.phase = quest.phase;
            own.deadline = quest.deadline;
            SharedQuests.Remember(own);
            _teamQuestKnown[(charaUid, questUid)] = Hash(LZ4Bytes.Create(own).Bytes);
            return;
        }

        var log = PersonalLogOf(owner);
        if (!log.TryGetValue(questUid, out var kept) || new LZ4Bytes { Bytes = kept }.Decompress<Quest>() is not { } before ||
            !KeepTerms(before, quest)) {
            return;
        }

        var bytes = LZ4Bytes.Create(quest).Bytes;
        log[questUid] = bytes;
        _teamQuestFromMate.Add(questUid);
        // (not back to the player who sent it)
        _teamQuestKnown[(charaUid, questUid)] = Hash(bytes);
    }

    /// <summary>
    ///     What a teammate's copy may not change: the kind of quest and what it pays, as its owner took it
    /// </summary>
    private static bool KeepTerms(Quest before, Quest after)
    {
        if (before.GetType() != after.GetType() || before.id != after.id) {
            return false;
        }

        after.rewardMoney = before.rewardMoney;
        after.bonusMoney = Math.Min(after.bonusMoney, Math.Max(before.bonusMoney, before.rewardMoney));
        after.difficulty = before.difficulty;
        after.dangerLv = before.dangerLv;
        after.lv = before.lv;
        return true;
    }

    /// <summary>
    ///     The owner of a shared quest told what it holds: it is the latest, its own game needs nothing back
    /// </summary>
    private void NoteOwnerTold(int owner, int questUid, byte[] bytes)
    {
        if (TeamSharedQuests.TryGetValue(questUid, out var known) && known == owner) {
            _teamQuestFromMate.Remove(questUid);
            _teamQuestKnown[(owner, questUid)] = Hash(bytes);
        }
    }

    /// <summary>
    ///     The host turns in a quest from its own journal: a teammate's copy is paid as its owner took it
    /// </summary>
    internal void HoldTeamQuestTerms(Quest quest)
    {
        if (IsZoneSession || !TeamQuests.IsCopy(quest) || !ElinDelta.TrustEnforced) {
            return;
        }

        if (!HoldToTerms(pc, quest)) {
            quest.rewardMoney = 0;
            quest.bonusMoney = 0;
        }
    }

    /// <summary>
    ///     The host completed a quest of its journal itself (QuestCompleteEvent): its own shared one, or a teammate's
    ///     copy, which is done for its owner too. The teammates get their share
    /// </summary>
    internal void OnLocalQuestComplete(Quest quest)
    {
        if (IsZoneSession || !TeamSharedQuests.TryGetValue(quest.uid, out var owner)) {
            return;
        }

        if (owner != pc.uid) {
            if (!MayUseTeamQuest(pc.uid, quest.uid, out _)) {
                return;
            }

            PersonalLogOf(owner).Remove(quest.uid);
            PersonalTerms.Remove(quest.uid);
        }

        CloseTeamQuest(quest, owner, pc.uid);
    }

    /// <summary>
    ///     A player away from this map turned in a shared quest there (its own or a teammate's), and had its reward
    ///     there: the quest is done for its owner, the teammates get their share
    /// </summary>
    internal void TeamQuestDoneAway(int peerId, int questUid, LZ4Bytes? data)
    {
        if (IsZoneSession || !TeamQuests.On || ActiveRemoteCharas.ContainsKey(peerId) || PlayerUidOf(peerId) is not (> 0 and var turner) ||
            !TeamSharedQuests.TryGetValue(questUid, out var owner)) {
            return;
        }

        if (turner != owner && !MayUseTeamQuest(turner, questUid, out _)) {
            EmpLog.Warning("Player {CharaUid} turned in quest {QuestUid}, not a shared quest of its team", turner, questUid);
            return;
        }

        Quest? quest;
        if (owner == pc.uid) {
            quest = game.quests.list.Find(q => q.uid == questUid && !TeamQuests.IsCopy(q));
            if (quest is not null) {
                // done for the host too, its reward is the turner's
                QuestCompleteEvent.CompleteQuietly(quest, false);
            }
        } else {
            var log = PersonalLogOf(owner);
            quest = log.TryGetValue(questUid, out var kept) ? new LZ4Bytes { Bytes = kept }.Decompress<Quest>() : data?.Decompress<Quest>();
            log.Remove(questUid);
            if (quest is not null && ElinDelta.TrustEnforced && !HoldToTerms(pc, quest)) {
                quest = null;
            }
        }

        PersonalTerms.Remove(questUid);
        if (quest is null || quest.uid != questUid) {
            TeamSharedQuests.Remove(questUid);
            return;
        }

        CloseTeamQuest(quest, owner, turner);
    }

    /// <summary>
    ///     A shared quest is done (turned in by <paramref name="turner" />, who had the reward): every other player of
    ///     the owner's team in the game gets the reward too (connected, wherever it is: in its bag on this map, in
    ///     the mail of the base elsewhere), and every game drops it
    /// </summary>
    private void CloseTeamQuest(Quest quest, int owner, int turner)
    {
        if (!TeamSharedQuests.Remove(quest.uid)) {
            return;
        }

        _teamQuestFromMate.Remove(quest.uid);

        var team = Teams.TeamOf(owner);
        var members = (team == Teams.Wanderers ? [] : Teams.MembersOf(team)).Append(owner).Distinct().ToArray();
        foreach (var member in members) {
            if (member != turner) {
                GiveTeamQuestShare(member, quest);
            }
        }

        // the quest leaves every journal it is in, the owner's included
        foreach (var (peer, uid) in TeamQuestPlayers().ToArray()) {
            var shows = _teamQuestShown.TryGetValue(uid, out var shown) && shown.Remove(quest.uid);
            _teamQuestKnown.Remove((uid, quest.uid));
            if (shows || uid == owner || members.Contains(uid)) {
                SendTeamQuests(peer, new() { Quests = [], Owners = [], Closed = [quest.uid] });
            }
        }

        EmpLog.Information("Shared quest {QuestUid} {QuestId} of player {Owner} turned in by {Turner}: shares for team {TeamId}",
            quest.uid, quest.id, owner, turner, team);
    }

    /// <summary>
    ///     The reward of a quest made again for one more player: what the quest drops (the game's own OnDropReward,
    ///     collected instead of dropped) and the fame it gives, not the karma of turning it in
    /// </summary>
    private void GiveTeamQuestShare(int member, Quest quest)
    {
        Chara? chara;
        var peer = -1;
        if (member == pc.uid) {
            if (ServerAvatar.Is(pc)) {
                return;
            }

            chara = pc;
        } else {
            // (connected players only: one not in the game gets nothing)
            peer = PeerOfChara(member);
            chara = peer >= 0 ? PlayerChara(peer) : null;
        }

        if (chara is not { isDead: false }) {
            return;
        }

        var things = new List<Thing>();
        try {
            using (QuestRewardPatch.Collect(things)) {
                using var _ = peer >= 0 ? (IDisposable?)PlayerStandIn.Share(this, peer) : null;
                using (ElinDelta.Simulate()) {
                    quest.OnDropReward();
                }
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Share of quest {QuestId} for player {CharaUid} could not be made", quest.id, member);
        }

        if (things.Count == 0) {
            Teams.Say(member, "emp_team_quest_share_done", quest.GetTitle());
            return;
        }

        var here = chara.IsAliveInCurrentZone && _map.charas.Contains(chara);
        var mailed = new List<Thing>();
        foreach (var thing in things) {
            if (here && !chara.things.IsFull(thing)) {
                using (ElinDelta.Simulate()) {
                    chara.AddThing(thing);
                }
            } else if (here) {
                CardCache.Add(thing);
                CardCache.CacheContainer(thing.things);
                using (ElinDelta.Simulate()) {
                    Delta.AddRemote(CardGenDelta.Create(thing));
                    _zone.AddCard(thing, chara.pos);
                }
            } else {
                mailed.Add(thing);
            }
        }

        if (mailed.Count > 0) {
            var parcel = ThingGen.CreateParcel(null, mailed.ToArray());
            parcel.c_idRefName = "emp_story_share".Loc(chara.NameSimple);
            TeamMail.Send(chara, parcel);
        }

        Teams.Say(member, here ? "emp_team_quest_share_got" : "emp_team_quest_share_mail", quest.GetTitle());
        EmpLog.Information("Shared quest {QuestId}: a share for player {CharaUid} ({Where})", quest.id, member, here ? "in its bag" : "by mail");
    }

    internal int TeamQuestPlayerUid(int peerId)
    {
        return PlayerUidOf(peerId);
    }
}
