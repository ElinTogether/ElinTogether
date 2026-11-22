using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using Newtonsoft.Json;

namespace ElinTogether.Helper;

/// <summary>
///     Council 2026-10-28, host option "Each player or team lives its own story" (experimental, off by default: one
///     story for all as before; always on for the official public server; dev/COUNCIL_2026-10-28_team_story.md). The story is the main quest (QuestMain, 0-700) and the exploration of
///     Nymelle (QuestExploration, 0-6), with every dialog choice, the key items, Loytel's debt and the extra tax (the
///     user's "etc.", 2026-10-28: what the story-owner's progress holds). The user, 2026-10-28: "each player
///     does the story at its own pace, and does it as a team". So the story has an OWNER: the player's team when it is
///     in a team (teammates share one story), otherwise the player alone (owner -chara uid: players without a team
///     never share one). The host keeps every owner's story in its save (<see cref="ElinNetHost.TeamStoryBundles" />);
///     every game, the host's own included, holds its owner's in its quest log while it plays. The save holds the WORLD
///     OWNER's (the host's own owner when the box was ticked, changed only by the host): a world opened without the mod,
///     or with the box off, goes on with that story (the key items: what any story got, council risk 8). Side stories
///     and the rest of the quest log stay the world's (their union is the world's anyway). Random and personal quests
///     are never part of it. Copied from
///     <see cref="TeamKnowledge" />
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class TeamOwnStory
{
    /// <summary>
    ///     The ledger's layout. A save written by a newer layout is left as it is: no team's story is swapped at all
    /// </summary>
    private const int Schema = 1;

    // the quests of the story, by id and by the type name QuestManager.completedTypes holds
    // (and Loytel's debt: its bills and rewards are the owner's; how it started, pre_debt and the runaway, is a side
    // story of the world)
    private static readonly HashSet<string> StoryIds = ["main", "exploration", "debt"];
    private static readonly HashSet<string> StoryTypes = [nameof(QuestMain), nameof(QuestExploration), nameof(QuestDebt)];

    // the dialog choices of the main story (Ash's and Fiama's dramas: setFlag ash1, tutorial1, fiama1). Every dialog
    // flag is the story's (what a drama remembers was said or chosen is that story's progress, the user 2026-10-28):
    // these alone were, in a story written before
    private static readonly string[] DialogIds = ["ash1", "tutorial1", "fiama1"];

    // what the story sets in Player.flags (QuestDebt.GiveReward: Loytel's mart and its level; Fiama's dreams as her
    // story goes on, Chara.ShowDialog "fiama")
    private static readonly System.Reflection.PropertyInfo[] StoryFlagProps = new[] {
        nameof(Player.Flags.gotLoytelMart), nameof(Player.Flags.loytelMartLv),
        nameof(Player.Flags.storyFiama), nameof(Player.Flags.fiamaFirstDream), nameof(Player.Flags.fiamaStoryBookGiven),
    }.Select(name => typeof(Player.Flags).GetProperty(name)!).ToArray();

    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    private static int[] State => ElinNetHost.TeamStoryBundleState;

    // (a ledger written by a newer version: one story for everyone)
    private static bool TooNew => KeepsBook && State[0] > Schema;

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules
    /// </summary>
    internal static bool On => !TooNew && (KeepsBook ? Wanted : NetSession.Instance.Rules is { TeamOwnStory: true });

    /// <summary>
    ///     The host's box, read as on whatever is stored while this game hosts the official public server (the user,
    ///     2026-10-28: there the story is separated by player and team)
    /// </summary>
    internal static bool Wanted => EmpConfig.Server.TeamOwnStory.Value || StoryOwner.OfficialHosting;

    // (an owner the ledger never heard of, a player new to the world: the story point of the tick)
    private const int SeedOwner = StoryOwner.Seed;

    /// <summary>
    ///     Whose story that player plays: its team's, or its own when it has none
    /// </summary>
    internal static int OwnerOf(int charaUid)
    {
        return StoryOwner.Of(charaUid);
    }

    private static bool Seeded => State[1] != 0;

    /// <summary>
    ///     The owner whose story the save keeps
    /// </summary>
    internal static int WorldOwner => State[2];

    // the game the ledger was read against, and the owner whose story its quest log holds (none: the world owner's, as
    // the save has it)
    private static Game? _game;
    private static int? _owner;

    // the box as the last frame read it (none: just after a save was read)
    private static bool? _wasWanted;

    /// <summary>
    ///     The owner whose story the local quest log holds, in the game that keeps the book; none while it holds the
    ///     world owner's as the save has it, or the box is off
    /// </summary>
    internal static int? LocalOwner => KeepsBook && ReferenceEquals(EClass.game, _game) ? _owner : null;

    internal static bool IsStory(Quest? quest)
    {
        return quest is QuestMain or QuestExploration or QuestDebt;
    }

    /// <summary>
    ///     Host option "Guilds per player or team" (off by default, the official server too: a guild is a place in a
    ///     town everyone walks into), with the story box: each owner's own membership, rank and contribution in the
    ///     four guilds, and its own guild quests (Guild.CurrentQuest). Off: the guilds are the world's, one membership
    ///     for the group as before
    /// </summary>
    internal static bool Guilds => On && (KeepsBook ? EmpConfig.Server.TeamOwnGuilds.Value : NetSession.Instance.Rules is { TeamOwnGuilds: true });

    /// <summary>
    ///     Host option "Story characters per team" (issue 63, on by default, always on for the public server), with the
    ///     story box: each owner has its own Loytel (<see cref="LoytelOf" />) and Farris (<see cref="FarrisOf" />). Off: one of each for
    ///     the world as before
    /// </summary>
    internal static bool CharaCopies => On && (KeepsBook ? CopiesWanted : NetSession.Instance.Rules is { StoryCharaCopies: true });

    internal static bool CopiesWanted => EmpConfig.Server.StoryCharaCopies.Value || StoryOwner.OfficialHosting;

    // a quest of the story's quest log: with the guilds, the guild quests too
    private static bool InLog(Quest? quest, bool guilds)
    {
        return IsStory(quest) || (guilds && quest is QuestGuild);
    }

    /// <summary>
    ///     Every frame, in the game that keeps the book: the box ticked gives every owner the world's story (once), the
    ///     box unticked gives the world the world owner's story
    /// </summary>
    internal static void Update()
    {
        if (!KeepsBook || !EClass.core.IsGameStarted || EClass.game?.quests is null || EClass.player is null) {
            return;
        }

        if (!ReferenceEquals(EClass.game, _game)) {
            // (a save just read: its quest log holds the world owner's story)
            _game = EClass.game;
            _owner = null;
            _wasWanted = null;
            if (State[0] > Schema) {
                EmpLog.Warning("The teams' story was written by a newer version of the mod ({Schema}): one story for everyone",
                    State[0]);
            }
        }

        if (TooNew) {
            return;
        }

        // (merged back only when the host unticks the box in this game: a world opened with the box reading off for another
        // reason, the official server without its switch, keeps its records)
        var unticked = _wasWanted == true && !Wanted;
        _wasWanted = Wanted;
        if (On && !Seeded) {
            Seed();
        } else if (!On && Seeded && unticked) {
            MergeBack();
            TellEveryone();
        }

        // (the save's values, before the host takes its own story: what an older ledger lacks)
        if (On && Seeded && _owner is null) {
            Upgrade();
        }

        // the host plays its own story like any player, its team's when it is in one
        if (On && Seeded && OwnerOf(ServerAvatar.LocalUid()) is var owner && owner != _owner) {
            Hold(owner);
        }

        if (On && Seeded && _owner is not null) {
            ForgetGoneTeams();
            LookAtLothria();
            CatchUp();
            DebtCatchUp();
            GuildsFlip();
        }
    }

    /// <summary>
    ///     The local quest log holds that owner's story: what it held goes back to its owner first
    /// </summary>
    private static void Hold(int team)
    {
        var was = _owner;
        Store(was ?? WorldOwner, Take());
        if (was is not null || team != WorldOwner) {
            Put(Of(team, true));
            Rebaseline();
        }

        _owner = team;
        if (was is not null) {
            Msg.SayRaw("emp_team_story_moved".lang());
        }

        EmpLog.Information("The host plays the story of owner {Owner}: main {Main}, exploration {Exploration}",
            team, EClass.game.quests.GetPhase<QuestMain>(), EClass.game.quests.GetPhase<QuestExploration>());
    }

    /// <summary>
    ///     That owner's story now: the local quest log's for the owner it holds, the ledger's for another (one the
    ///     ledger never heard of: the story point of the tick)
    /// </summary>
    internal static StoryBundle Of(int team, bool stored = false)
    {
        return !stored && team == LocalOwner ? Take() : Stored(team) ?? Stored(SeedOwner) ?? Stored(WorldOwner) ?? Take();
    }

    /// <summary>
    ///     What that player's owner holds, for its copy of the world; none when the box is off or the copy holds it
    ///     already (the host's own story)
    /// </summary>
    internal static StoryBundle? For(Chara chara)
    {
        return LocalOwner is { } local && On && OwnerOf(chara.uid) is var owner && owner != local ? Of(owner) : null;
    }

    /// <summary>
    ///     A guest: what its game holds now, to keep in the copy of the world coming in (another player's zone); none
    ///     when the box is off
    /// </summary>
    internal static StoryBundle? Carry()
    {
        return On && EClass.core.IsGameStarted && EClass.game?.quests is not null && EClass.player is not null ? Take() : null;
    }

    /// <summary>
    ///     A guest's game: the host says what its team's story is now (the box unticked: the world owner's)
    /// </summary>
    internal static void Hear(StoryBundle bundle, bool line)
    {
        if (EClass.game?.quests is null || EClass.player is null) {
            return;
        }

        Put(bundle);
        Rebaseline(bundle.Guilds is not null);
        if (line) {
            Msg.SayRaw("emp_team_story_moved".lang());
        }

        EmpLog.Information("The host says where this player's team stands in the story: main {Main}, exploration {Exploration}",
            bundle.Main, bundle.Exploration);
    }

    // what the quest log and the dialogs hold now is what this game was given: nothing to tell
    private static void Rebaseline(bool guilds = false)
    {
        guilds |= Guilds;
        foreach (var quest in EClass.game.quests.list.Where(q => InLog(q, guilds))) {
            SharedQuests.Remember(quest);
            quest.UpdateJournal();
        }

        DialogFlagSync.Relearn(id => IsStoryFlag(id) || (guilds && id.StartsWith("g:")));
    }

    /// <summary>
    ///     The box was unticked: every player is told the world owner's story (its guilds too: the world's)
    /// </summary>
    private static void TellEveryone()
    {
        if (NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } host) {
            return;
        }

        var world = Take(true);
        foreach (var peer in host.ActiveRemoteCharas.Keys) {
            host.SendDeltaTo(peer, new StoryBundleDelta { Bundle = world });
        }
    }

    // ---- teams changing

    /// <summary>
    ///     In the game that keeps the book: that player went from one team to another (or none). Joining a team: it
    ///     plays the team's story, or the team takes its story when the team has none yet (its first member). Leaving
    ///     for no team: it keeps a copy of the team's story as its own. One quiet line in its journal. The host's own
    ///     player: on the next frame (<see cref="Update" />)
    /// </summary>
    internal static void Moved(Chara chara, int fromTeam, int toTeam)
    {
        var from = StoryOwner.Of(fromTeam, chara.uid);
        var to = StoryOwner.Of(toTeam, chara.uid);
        if (LocalOwner is null || !On || from == to) {
            return;
        }

        var hasOwn = Stored(to) is not null || to == LocalOwner;
        if (to < 0 && hasOwn) {
            // (leaving or kicked, with a solo story of its own: the better of both, never in place)
            Store(to, Leaving(Of(to), Of(from)));
        } else if (StoryOwner.TakesMovers(to, hasOwn)) {
            Store(to, Of(from));
        }

        EmpLog.Information("Player {CharaUid} went from story {From} to story {To}", chara.uid, from, to);
        if (NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false } host &&
            host.ActiveRemoteCharas.FirstOrDefault(p => p.Value == chara) is { Value: not null } player) {
            host.SendDeltaTo(player.Key, new StoryBundleDelta { Bundle = Of(to), Line = true });
        }
    }

    /// <summary>
    ///     What a player keeps when it leaves a team it had a solo story before: the quest log, the offers, the phases,
    ///     the finished quests, the dialog and the flags whole from the more advanced of the two (the team's on a tie),
    ///     the key items of both (the most of each), the greater debt and extra tax, per guild the higher place. A new
    ///     bundle: neither argument is changed
    /// </summary>
    internal static StoryBundle Leaving(StoryBundle solo, StoryBundle team)
    {
        var ahead = solo.Main > team.Main || (solo.Main == team.Main && solo.Exploration > team.Exploration) ? solo : team;
        var merged = new StoryBundle {
            Quests = [..ahead.Quests],
            Offers = [..ahead.Offers],
            Completed = [..ahead.Completed],
            Dialog = new(ahead.Dialog),
            AllDialog = ahead.AllDialog,
            Main = ahead.Main,
            Exploration = ahead.Exploration,
            Flags = ahead.Flags is null ? null : new(ahead.Flags),
        };
        if (solo.KeyItems is not null || team.KeyItems is not null) {
            merged.KeyItems = [];
            foreach (var items in new[] { solo.KeyItems, team.KeyItems }) {
                foreach (var (item, count) in items ?? []) {
                    merged.KeyItems[item] = System.Math.Max(merged.KeyItems.GetValueOrDefault(item), count);
                }
            }
        }

        merged.Debt = MaxOf(solo.Debt, team.Debt);
        merged.ExtraTax = MaxOf(solo.ExtraTax, team.ExtraTax);
        if (solo.Guilds is not null || team.Guilds is not null) {
            merged.Guilds = [];
            foreach (var guilds in new[] { team.Guilds, solo.Guilds }) {
                foreach (var (id, place) in guilds ?? []) {
                    if (!merged.Guilds.TryGetValue(id, out var have) || have.Length < 3 || (place.Length >= 3 &&
                            (place[1] > have[1] || (place[1] == have[1] && place[2] > have[2])))) {
                        merged.Guilds[id] = [..place];
                    }
                }
            }
        }

        return merged;
    }

    private static int? MaxOf(int? a, int? b)
    {
        return a is null ? b : b is null ? a : System.Math.Max(a.Value, b.Value);
    }

    /// <summary>
    ///     A team that is no more is out of the ledger (not the world owner: the save keeps its story)
    /// </summary>
    private static void ForgetGoneTeams()
    {
        // (every frame: nothing is made unless a team is gone)
        List<int>? gone = null;
        foreach (var t in ElinNetHost.TeamStoryBundles.Keys) {
            if (StoryOwner.IsGoneTeam(t) && t != WorldOwner && t != _owner) {
                (gone ??= []).Add(t);
            }
        }

        if (gone is null) {
            return;
        }

        foreach (var team in gone) {
            ElinNetHost.TeamStoryBundles.Remove(team);
            EmpLog.Information("Team {TeamId} is gone: its story is out of the ledger", team);
        }
    }

    /// <summary>
    ///     The box was ticked: every team and every player without one starts at the story point the world is at (a
    ///     player new to the world later too). The world owner is the host's
    /// </summary>
    private static void Seed()
    {
        var world = Take();
        var host = OwnerOf(ServerAvatar.LocalUid());
        foreach (var owner in StoryOwner.All().Append(host).Append(SeedOwner).Distinct()) {
            Store(owner, world);
        }

        ElinNetHost.TeamStoryBundleState = [Schema, 1, host, 0, Guilds ? 1 : 0];
        _owner = host;
        EmpLog.Information("Each player or team lives its own story: all start at main {Main}, exploration {Exploration}; the world owner is {Owner}",
            world.Main, world.Exploration, host);
    }

    /// <summary>
    ///     The box was unticked: the world goes on with the world owner's story, the ledger is emptied (ticked again,
    ///     every team starts again from the world's)
    /// </summary>
    private static void MergeBack()
    {
        var keys = KeyItemsOfAll();
        if (LocalOwner is { } local && local != WorldOwner && Stored(WorldOwner) is { } world) {
            Put(world);
        }

        // (the key items: everything any story got, the most of each; council risk 8)
        foreach (var (item, count) in keys) {
            EClass.player.keyItems[item] = count;
        }

        // (Ash and Fiama waited for the other stories: the world's alone counts now)
        if (State[3] != 0 && EClass.game.quests.GetPhase<QuestMain>() >= QuestMain.AfterAshLeaveHome) {
            LeaveForLothria();
        }

        _owner = null;
        ElinNetHost.TeamStoryBundles.Clear();
        ElinNetHost.TeamStoryBundleState = [Schema, 0, 0, 0, 0];
        EmpLog.Information("One story for the world again: the world owner's");
    }

    // every story's key items, the most of each (the seed entry is no story anyone plays)
    private static Dictionary<int, int> KeyItemsOfAll()
    {
        var all = new Dictionary<int, int>(EClass.player.keyItems);
        foreach (var owner in ElinNetHost.TeamStoryBundles.Keys.Where(o => o != SeedOwner && o != LocalOwner)) {
            foreach (var (item, count) in Stored(owner)?.KeyItems ?? []) {
                all[item] = System.Math.Max(count, all.GetValueOrDefault(item));
            }
        }

        return all;
    }

    // ---- another owner's story, for a moment

    // the owner whose story the quest log holds for the length of an As scope
    private static int? _as;

    /// <summary>
    ///     The owner whose story the local quest log holds right now: the one an As scope put in, or the local one
    /// </summary>
    internal static int? CurrentOwner => _as ?? LocalOwner;

    internal static bool Swapped => _as is not null;

    /// <summary>
    ///     In the game that keeps the book: for the length of the scope the quest log holds that owner's story (what a
    ///     guest's step checks, moves and triggers is read against its own story), stored back at the end and the exact
    ///     objects of the local story put back. What the diff loops have to tell is told before (from the local story),
    ///     nothing is re-baselined after. Nothing when it is the local owner (one team of friends: never a swap)
    /// </summary>
    internal static ScopeExit As(int owner)
    {
        if (LocalOwner is not { } local || !On || !Seeded || _as is not null || owner == local || EClass.game?.quests is null) {
            return new();
        }

        DialogFlagSync.TellChanges();
        PersonalQuests.Tick();
        SharedQuests.TellChanges();

        var snap = Snap();
        Put(Of(owner, true));
        _as = owner;
        return new() {
            OnExit = () => {
                try {
                    Store(owner, Take());
                } finally {
                    Restore(snap);
                    _as = null;
                }
            },
        };
    }

    /// <summary>
    ///     On the host, around the permit, the refusal and the apply of a guest's story message: the sender's story
    /// </summary>
    internal static ScopeExit AsSender(ElinNetHost host, int peer)
    {
        return !host.IsZoneSession && host.PlayerChara(peer) is { } sender ? As(OwnerOf(sender.uid)) : new();
    }

    /// <summary>
    ///     A zone a player keeps never holds a visitor's story (council risk 7): a story step of a visitor of another
    ///     owner is not applied there (the visitor's own game holds it)
    /// </summary>
    internal static bool KeeperDrops(ElinNetHost host, ElinDelta delta)
    {
        if (!host.IsZoneSession || !On || !IsStoryDelta(delta) || host.PlayerChara(delta.OriginPeer) is not { } sender ||
            OwnerOf(sender.uid) == OwnerOf(ServerAvatar.LocalUid())) {
            return false;
        }

        EmpLog.Debug("A visitor's story step {DeltaType} is not this zone's story: left to its own game", delta.GetType().Name);
        return true;
    }

    private static readonly HashSet<string> StoryFlags = [..DialogIds.Select(id => "d:" + id), ..StoryFlagProps.Select(p => "f:" + p.Name)];

    /// <summary>
    ///     A value the story remembers (DialogFlagSync's id): a dialog flag, a key item, the debt, the extra tax, a flag
    ///     the story sets
    /// </summary>
    internal static bool IsStoryFlag(string id)
    {
        return StoryFlags.Contains(id) || id.StartsWith("d:") || id.StartsWith("k:") || id is "p:debt" or "p:extraTax" ||
               (id.StartsWith("g:") && Guilds);
    }

    /// <summary>
    ///     A message about the story: a step, a start, an end, a change of the main quest or the exploration, a dialog
    ///     choice of the story
    /// </summary>
    internal static bool IsStoryDelta(ElinDelta delta)
    {
        return delta switch {
            QuestChangePhaseDelta q => IsStoryQuest(q.Uid, q.Id),
            QuestUpdateDelta q => IsStoryQuest(q.Uid, q.Id),
            QuestCompleteDelta q => IsStoryQuest(q.Uid, q.Id),
            QuestStartDelta q => IsStoryQuest(q.Uid, null),
            QuestFailDelta q => IsStoryQuest(q.Uid, null),
            DialogFlagDelta f => IsStoryFlag(f.Id),
            ExtraTaxDelta => true,
            _ => false,
        };
    }

    private static bool IsStoryQuest(int uid, string? id)
    {
        var guilds = Guilds;
        if (id is not null) {
            return StoryIds.Contains(id) || (guilds && id.StartsWith("guild_"));
        }

        var quests = EClass.game?.quests;
        return InLog(quests?.list.Find(q => q.uid == uid) ?? quests?.globalList.Find(q => q.uid == uid), guilds);
    }

    /// <summary>
    ///     On the host of the world: the owner whose players alone hear that message (a story message, while the box is
    ///     on); none when everyone does
    /// </summary>
    internal static int? RouteOf(ElinNetHost host, ElinDelta delta)
    {
        return !host.IsZoneSession && On && Seeded && CurrentOwner is { } owner && IsStoryDelta(delta) ? owner : null;
    }

    /// <summary>
    ///     A message the host sends to everyone: a story message goes to the players of the story it is about only
    ///     (relays to teammates, or to the player alone). True when it was sent here
    /// </summary>
    internal static bool Divert(ElinDeltaManager manager, ElinDelta delta)
    {
        // a zone a player keeps: its own story deltas go to the visitors of its own owner only (the other visitors play
        // another story, a delta of this one would overwrite it)
        if (NetSession.Instance.ZoneSession is ElinNetHost { IsZoneSession: true } zone && ReferenceEquals(zone.Delta, manager) &&
            On && IsStoryDelta(delta)) {
            var keeper = OwnerOf(ServerAvatar.LocalUid());
            foreach (var (peer, visitor) in zone.PeerPlayers()) {
                if (OwnerOf(visitor.uid) == keeper) {
                    zone.SendDeltaTo(peer, delta);
                }
            }

            return true;
        }

        if (NetSession.Instance.Transport is not ElinNetHost host || !ReferenceEquals(host.Delta, manager) ||
            RouteOf(host, delta) is not { } owner) {
            return false;
        }

        foreach (var (peer, uid) in host.PeerCharas()) {
            if (OwnerOf(uid) == owner) {
                host.SendDeltaTo(peer, delta);
            }
        }

        return true;
    }

    /// <summary>
    ///     The guardian of Nymelle moves the exploration on as it dies (Chara.Die, "isygarad"), in the game that runs
    ///     its map: the story of the player who killed it, not the host's
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    private static void BeforeDie(Chara __instance, Card? origin, out ScopeExit? __state)
    {
        __state = null;
        if (__instance.id == "isygarad" && KeepsBook && PlayerKarma.PlayerBehind(origin) is { } killer) {
            __state = As(OwnerOf(killer.uid));
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    private static void AfterDie(ScopeExit? __state)
    {
        __state?.Dispose();
    }

    // ---- who each game shows: Ash, Fiama, Farris

    /// <summary>
    ///     The three actors of the story are single characters of the world, where the story that moved them last put
    ///     them. Each game shows one where its own story says it is, and hides it elsewhere (not drawn, not talked to):
    ///     Farris in Nymelle until met, at home or in a base after; Fiama by the crystal between the guardian and her
    ///     talk; Ash and Fiama in Lothria once the main quest left home. In a world of one story everything is where the
    ///     story says: nothing is ever hidden
    /// </summary>
    internal static bool Shown(Chara chara)
    {
        // (issue 63: a Loytel or Farris of another owner's story is not this game's, even in a party)
        if (chara.id is "loytel" or "farris" && CharaCopies && ViewerOwner is { } viewer) {
            var tag = chara.GetInt(StoryOwnerKey);
            if (tag != 0 && tag != viewer) {
                return false;
            }

            // (a guest: a Farris no story took yet is not shown next to its own)
            if (tag == 0 && chara.id == "farris" && !KeepsBook && OwnCopyHere("farris", viewer, EClass._map?.charas ?? []) is not null) {
                return false;
            }
        }

        if (chara.id == "loytel") {
            return true;
        }

        if (chara.id is not ("ashland" or "fiama" or "farris") || !On || chara.IsPCParty || EClass.game?.quests is not { } quests ||
            EClass.game.IsSurvival) {
            return true;
        }

        var exploration = quests.GetPhase<QuestExploration>();
        var main = quests.GetPhase<QuestMain>();
        var zone = chara.currentZone;
        return chara.id switch {
            "farris" => zone is Zone_Nymelle ? exploration <= QuestExploration.Started : exploration >= QuestExploration.AfterMeetFarris,
            "fiama" => zone is Zone_Nymelle
                ? exploration is >= QuestExploration.AfterDefeatNymelleBoss and < QuestExploration.AfterCrystalTalk
                : zone?.id != "lothria" || main >= QuestMain.AfterAshLeaveHome,
            _ => zone?.id != "lothria" || main >= QuestMain.AfterAshLeaveHome,
        };
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.CanSee))]
    private static void HideOutOfStory(Chara c, ref bool __result)
    {
        if (__result && !Shown(c)) {
            __result = false;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.ShowDialog), new System.Type[0])]
    private static bool NoTalkOutOfStory(Chara __instance)
    {
        return Shown(__instance);
    }

    // ---- Ash and Fiama leave for Lothria once every story is there

    private static float _nextLothria;

    /// <summary>
    ///     The end of the exploration moves Ash and Fiama to Lothria for good, for every story: while another story is
    ///     not that far (below 700), they stay home, the story itself moves on. The move is made once no story waits
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DramaOutcome), nameof(DramaOutcome.QuestExploration_AfterComplete))]
    private static bool AfterExplorationComplete()
    {
        // (a guest's game: the host moves them, when it can)
        if (!On || (KeepsBook && !AnyBehind())) {
            return true;
        }

        var quests = EClass.game.quests;
        if (quests.Main is null) {
            quests.Start("main");
        }

        quests.Main!.ChangePhase(QuestMain.AfterAshLeaveHome);
        if (KeepsBook) {
            State[3] = 1;
            EmpLog.Information("The main quest left home in story {Owner}: Ash and Fiama wait for the other stories", CurrentOwner);
        }

        return false;
    }

    // another story is below 700: the stories of the players connected now (a visitor gone for good, or the dedicated
    // server's own never-moving story, wait for nobody). The seed entry is no story anyone plays
    private static bool AnyBehind()
    {
        if (!Seeded) {
            return false;
        }

        var current = CurrentOwner;
        var owners = (NetSession.Instance.Transport as ElinNetHost)?.PeerCharas().Select(p => OwnerOf(p.CharaUid)).ToList() ?? [];
        if (!EmpServer.Requested) {
            owners.Add(OwnerOf(ServerAvatar.LocalUid()));
        }

        return owners.Distinct().Where(owner => owner != SeedOwner && owner != current)
            .Select(Stored).Any(bundle => bundle is { Main: < QuestMain.AfterAshLeaveHome });
    }

    private static void LookAtLothria()
    {
        if (State[3] == 0 || UnityEngine.Time.unscaledTime < _nextLothria) {
            return;
        }

        _nextLothria = UnityEngine.Time.unscaledTime + 5f;
        // (the dedicated server's own story never moves: only the players' count)
        if ((!EmpServer.Requested && EClass.game.quests.GetPhase<QuestMain>() < QuestMain.AfterAshLeaveHome) || AnyBehind()) {
            return;
        }

        LeaveForLothria();
    }

    // DramaOutcome.QuestExploration_AfterComplete, the part about the world
    private static void LeaveForLothria()
    {
        State[3] = 0;
        if (EClass.game.cards.globalCharas.Find("ashland") is { } ash) {
            ash.MoveHome("lothria", 40, 49);
            EClass.game.quests.RemoveAll(ash);
        }

        if (EClass.game.cards.globalCharas.Find("fiama") is { } fiama) {
            fiama.MoveHome("lothria", 46, 56);
            EClass.game.quests.RemoveAll(fiama);
        }

        EmpLog.Information("Every story is past the exploration: Ash and Fiama leave for Lothria");
    }

    // ---- a later story on a floor another story cleared

    private static float _nextCatchUp;

    /// <summary>
    ///     Nymelle's floors play their story once (the guardian and Fiama only come at the first visit, Farris is
    ///     recruited into one base): a player whose story is behind, standing on a floor another story already
    ///     cleared, would be stuck there. Its story takes the step on (no second recruit; with the story characters each story
    ///     meets its own Farris instead), with a line, and its players
    ///     get what the story gave meanwhile (the ledger of story rewards, one parcel each)
    /// </summary>
    private static void CatchUp()
    {
        if (UnityEngine.Time.unscaledTime < _nextCatchUp || NetSession.Instance.Transport is ElinNetHost { IsZoneSession: true }) {
            return;
        }

        _nextCatchUp = UnityEngine.Time.unscaledTime + 2f;
        var host = NetSession.Instance.Transport as ElinNetHost;
        var players = (host?.PeerCharas().Select(p => p.CharaUid) ?? []).Append(EClass.pc.uid).Where(uid => !ServerAvatar.IsUid(uid)).Distinct()
            .Select(uid => EClass.game.cards.globalCharas.Find(uid)).OfType<Chara>().ToList();
        foreach (var owner in players.Select(p => (Player: p, Owner: OwnerOf(p.uid))).Where(p => p.Player.currentZone is Zone_Nymelle)
                     .GroupBy(p => p.Owner)) {
            var zone = (Zone_Nymelle)owner.First().Player.currentZone;
            var mine = ExplorationOf(owner.Key);
            var best = ElinNetHost.TeamStoryBundles.Keys.Append(LocalOwner ?? SeedOwner).Distinct()
                .Where(o => o != owner.Key && o != SeedOwner).Select(ExplorationOf).DefaultIfEmpty(-1).Max();
            // (issue 63: each story meets its own Farris, made on the floor this game plays; a floor a guest holds, that
            // guest's game makes it, NymelleFarrisRepair)
            if (zone.lv == -2 && CharaCopies && mine <= QuestExploration.Started && zone == EClass._zone && FarrisOf(owner.Key, false) is null) {
                MakeFarris(owner.Key, zone);
            }

            var what = zone.lv switch {
                -2 when !CharaCopies && mine == QuestExploration.Started && best >= QuestExploration.AfterMeetFarris &&
                        EClass.game.cards.globalCharas.Find("farris") is { } farris && farris.currentZone != zone => "farris",
                _ when zone.IsBossLv && mine == QuestExploration.AfterMeetFarrisAtHome && best >= QuestExploration.AfterDefeatNymelleBoss => "boss",
                _ when zone.IsCrystalLv && mine is >= QuestExploration.AfterDefeatNymelleBoss and < QuestExploration.AfterCrystalTalk &&
                       best >= QuestExploration.AfterCrystalTalk &&
                       EClass.game.cards.globalCharas.Find("fiama")?.currentZone != zone => "crystal",
                _ => null,
            };
            if (what is not null) {
                CatchUp(owner.Key, what, owner.Select(p => p.Player).ToList());
            }
        }
    }

    // the exploration's phase in that owner's story (999 done, -1 not started)
    private static int ExplorationOf(int owner)
    {
        return owner == CurrentOwner ? EClass.game.quests.GetPhase<QuestExploration>() : Stored(owner)?.Exploration ?? -1;
    }

    private static void CatchUp(int owner, string what, List<Chara> players)
    {
        string[] steps;
        using (As(owner)) {
            var quests = EClass.game.quests;
            if (quests.Get<QuestExploration>() is not { } exploration) {
                return;
            }

            switch (what) {
                case "farris":
                    // (QuestExploration_MeetFarris and _MeetFarris2, without moving or recruiting him again)
                    exploration.ChangePhase(QuestExploration.AfterMeetFarris);
                    exploration.ChangePhase(QuestExploration.AfterMeetFarrisAtHome);
                    if (quests.Main is null) {
                        quests.Start("main");
                    }

                    if (quests.Main!.phase < QuestMain.AfterMeetFarris) {
                        quests.Main.ChangePhase(QuestMain.AfterMeetFarris);
                    }

                    steps = ["exploration:1", "exploration:2", "main:300"];
                    break;
                case "boss":
                    exploration.ChangePhase(QuestExploration.AfterDefeatNymelleBoss);
                    steps = ["exploration:3"];
                    break;
                default:
                    exploration.ChangePhase(QuestExploration.AfterCrystalTalk);
                    steps = ["exploration:5"];
                    break;
            }
        }

        EmpLog.Information("Story {Owner} caught up on Nymelle ({What}): another story cleared that floor", StoryOwner.Name(owner), what);
        var line = $"emp_team_story_caught_up_{what}".lang();
        var host = NetSession.Instance.Transport as ElinNetHost;
        foreach (var player in StoryPlayers(owner)) {
            foreach (var step in steps) {
                NoteHad(player.uid, step);
            }

            if (player.IsPC) {
                Msg.SayRaw(line);
            } else if (host is not null) {
                foreach (var (to, _) in host.PeerCharas().Where(p => p.CharaUid == player.uid)) {
                    host.SendDeltaTo(to, new MsgSayDelta { Text = line, R = 1f, G = 1f, B = 1f, A = 1f });
                }
            }

            ElinNetHost.CatchUpStoryShares(player);
        }
    }

    // the players of that story this game knows of, connected or here
    private static IEnumerable<Chara> StoryPlayers(int owner)
    {
        var host = NetSession.Instance.Transport as ElinNetHost;
        return (host?.PeerCharas().Select(p => p.CharaUid) ?? []).Append(EClass.pc.uid).Where(uid => !ServerAvatar.IsUid(uid)).Distinct()
            .Where(uid => OwnerOf(uid) == owner)
            .Select(uid => EClass.game.cards.globalCharas.Find(uid)).OfType<Chara>();
    }

    // ---- the debt and the extra tax

    /// <summary>
    ///     The extra tax the lands of that team are billed each month (TeamBills): its story's; the lands of no team,
    ///     the host's own while it plays without a team. None while the box is off (the world's, on the first bill)
    /// </summary>
    internal static int? ExtraTaxOf(int team)
    {
        if (LocalOwner is not { } local || !On || !Seeded) {
            return null;
        }

        if (team != Teams.Wanderers) {
            return Of(team).ExtraTax ?? 0;
        }

        return local < 0 ? EClass.player.extraTax : 0;
    }

    private static float _nextDebt;

    /// <summary>
    ///     Loytel's debt starts once in the world (the runaway's end, Chara.ShowDialog "loytel"): Loytel joins that
    ///     story's base and the debt quest is that story's. A story that has no debt quest once the world's runaway is
    ///     over gets one, already started (Loytel stays where the first story put him), with a line
    /// </summary>
    private static void DebtCatchUp()
    {
        if (UnityEngine.Time.unscaledTime < _nextDebt || NetSession.Instance.Transport is ElinNetHost { IsZoneSession: true }) {
            return;
        }

        _nextDebt = UnityEngine.Time.unscaledTime + 10f;
        var copies = CharaCopies;
        var world = copies ? null : EClass.game.cards.globalCharas.Find("loytel");
        if (!EClass.game.quests.IsCompleted("pre_debt_runaway") || (!copies && world is null)) {
            return;
        }

        var host = NetSession.Instance.Transport as ElinNetHost;
        var owners = (host?.PeerCharas().Select(p => p.CharaUid) ?? []).Append(EClass.pc.uid).Where(uid => !ServerAvatar.IsUid(uid)).Select(OwnerOf).Distinct().ToList();
        foreach (var owner in owners) {
            // (issue 63: each story its own Loytel first, a story of an old world already in debt too)
            if ((copies ? LoytelOf(owner, true) : world) is not { } loytel) {
                continue;
            }

            if (HasDebt(owner)) {
                // (an old world: a debt started before the copies names the world's Loytel, hidden from this story now)
                if (copies && owner == CurrentOwner && EClass.game.quests.Get<QuestDebt>() is { } debt && debt.person?.uidChara != loytel.uid) {
                    debt.SetClient(loytel, false);
                    EmpLog.Information("Story {Owner}'s debt now names its own Loytel", StoryOwner.Name(owner));
                } else if (copies && owner != CurrentOwner) {
                    using (As(owner)) {
                        if (EClass.game.quests.Get<QuestDebt>() is { } other && other.person?.uidChara != loytel.uid) {
                            other.SetClient(loytel, false);
                            EmpLog.Information("Story {Owner}'s debt now names its own Loytel", StoryOwner.Name(owner));
                        }
                    }
                }

                continue;
            }

            using (As(owner)) {
                var quest = Quest.Create("debt").SetClient(loytel, false);
                EClass.game.quests.list.Insert(0, quest);
            }

            EmpLog.Information("Story {Owner} has no debt quest after the runaway: it gets one", StoryOwner.Name(owner));
            var line = (copies ? "emp_team_story_own_loytel" : "emp_team_story_caught_up_debt").lang();
            foreach (var player in StoryPlayers(owner)) {
                if (player.IsPC) {
                    Msg.SayRaw(line);
                } else if (host is not null) {
                    foreach (var (to, _) in host.PeerCharas().Where(p => p.CharaUid == player.uid)) {
                        host.SendDeltaTo(to, new MsgSayDelta { Text = line, R = 1f, G = 1f, B = 1f, A = 1f });
                    }
                }
            }

            // the players of that story hear their story whole: the quest log changed in the host's ledger only
            if (host is not null && owner != CurrentOwner) {
                foreach (var (to, _) in host.PeerCharas().Where(p => OwnerOf(p.CharaUid) == owner)) {
                    host.SendDeltaTo(to, new StoryBundleDelta { Bundle = Of(owner) });
                }
            }
        }
    }

    // that owner's story has the debt quest, started, offered or done
    private static bool HasDebt(int owner)
    {
        if (owner == CurrentOwner) {
            var quests = EClass.game.quests;
            return quests.list.Any(q => q is QuestDebt) || quests.globalList.Any(q => q is QuestDebt) || quests.IsCompleted("debt");
        }

        var bundle = Of(owner, true);
        return bundle.Completed.Contains("debt") || Read(bundle.Quests).Concat(Read(bundle.Offers)).Any(q => q is QuestDebt);
    }

    // ---- each story its own Loytel and Farris (issue 63, "Story characters per team")

    // (int keys: a string key on "the player" also writes the shared dialog flags)
    // the story owner a Loytel or Farris belongs to; none on the world's one before a story took him
    private const int StoryOwnerKey = 47110003;

    // 1 on a Loytel or Farris made for a story, none on the world's own
    private const int StoryCopyKey = 47110004;

    // the owner whose Loytel and Farris this game shows and finds: on the host the story being played, on a guest its own
    private static int? ViewerOwner => KeepsBook ? CurrentOwner : EClass.pc is { } pc ? OwnerOf(pc.uid) : null;

    private static bool _debtStarting;
    private static bool _finding;
    private static float _waitSince = -1f;

    // (QuestDebt.OnStart: the story starting its debt takes the world's Loytel when no story has him yet)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestDebt), nameof(QuestDebt.OnStart))]
    private static void BeforeDebtStart()
    {
        _debtStarting = true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(QuestDebt), nameof(QuestDebt.OnStart))]
    private static void AfterDebtStart()
    {
        _debtStarting = false;
    }

    /// <summary>
    ///     The game looks Loytel and Farris up by id (QuestDebt, their dialogs, the quests they give, Vernis): the one of
    ///     the story being played, when it has one. A Loytel copy is made here only as a debt starts (elsewhere the game
    ///     may be walking the global charas), the others in <see cref="DebtCatchUp" />. Farris: never another story's;
    ///     none until this story's own came in Nymelle, unless the story is past meeting him (a story caught up before
    ///     the copies gets one as met)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardManager.GlobalCharaList), nameof(CardManager.GlobalCharaList.Find), typeof(string))]
    private static bool FindOwnStoryChara(string id, ref Chara? __result)
    {
        if (id is not ("loytel" or "farris") || _finding || !CharaCopies || EClass.game?.cards is null || ViewerOwner is not { } owner) {
            return true;
        }

        _finding = true;
        try {
            // (a story with no Loytel yet gets its own here: falling back to the game's lookup handed it another
            // story's, which pre_debt and Vernis' quests then moved away from that story's base)
            __result = !KeepsBook ? OwnCopyHere(id, owner, EClass.game.cards.globalCharas.Values.Concat(EClass._map?.charas ?? [])) :
                // (Farris too: QuestVernis step 5 uses the lookup without a null check)
                id == "loytel" ? LoytelOf(owner, true) : FarrisOf(owner, true);
        } finally {
            _finding = false;
        }

        return __result is null && id == "loytel";
    }

    // that owner's Loytel or Farris among these (a guest: among what the host sent it)
    private static Chara? OwnCopyHere(string id, int owner, IEnumerable<Chara> charas)
    {
        return charas.FirstOrDefault(c => c.id == id && c.GetInt(StoryOwnerKey) == owner);
    }

    /// <summary>
    ///     In the game that keeps the book: that owner's own Loytel. Until the runaway is over the world's one is
    ///     everyone's (none here). Then the world's Loytel is the story's that took him (its debt started him; in an
    ///     old world whose companion he is, whose land he lives in, else the world owner's), and with make every other
    ///     owner gets a copy of its own (<see cref="ElinNetHost.StoryLoytels" />)
    /// </summary>
    internal static Chara? LoytelOf(int owner, bool make)
    {
        var charas = EClass.game.cards.globalCharas;
        var mine = ElinNetHost.StoryLoytels;
        if (Mapped(mine, "loytel", owner) is { } own) {
            return own;
        }

        if (!EClass.game.quests.IsCompleted("pre_debt_runaway") ||
            charas.Values.FirstOrDefault(c => c.id == "loytel" && c.GetInt(StoryCopyKey) == 0) is not { } world) {
            return null;
        }

        if (!mine.ContainsValue(world.uid)) {
            // (still waiting in Olvina for the debt start of the story that ended the runaway: two minutes at most)
            if (!_debtStarting && world.noMove) {
                var now = UnityEngine.Time.unscaledTime;
                if (_waitSince < 0f || now < _waitSince) {
                    _waitSince = now;
                }

                if (now - _waitSince < 120f) {
                    return null;
                }
            }

            var taker = _debtStarting ? owner : TakerOf(world);
            if (!mine.ContainsKey(taker)) {
                Tag(mine, world, taker, false);
                _waitSince = -1f;
                EmpLog.Information("The world's Loytel is the story of {Owner}'s", Name(taker));
                if (taker == owner) {
                    return world;
                }
            }
        }

        return Kept(mine, "loytel", owner) ?? (make ? MakeLoytel(owner) : null);
    }

    // that owner's entry in the map, while its character is still in the world
    private static Chara? Mapped(Dictionary<int, int> mine, string id, int owner)
    {
        if (!mine.TryGetValue(owner, out var uid)) {
            return null;
        }

        if (EClass.game.cards.globalCharas.Find(uid) is { } own && own.id == id) {
            return own;
        }

        mine.Remove(owner);
        return null;
    }

    // (a copy whose map entry was lost is found again by its tags, never made twice)
    private static Chara? Kept(Dictionary<int, int> mine, string id, int owner)
    {
        if (EClass.game.cards.globalCharas.Values.FirstOrDefault(c => c.id == id && c.GetInt(StoryCopyKey) == 1 && c.GetInt(StoryOwnerKey) == owner) is not
            { } kept) {
            return null;
        }

        mine[owner] = kept.uid;
        return kept;
    }

    // that owner's from now on (a guest's own Farris: no map, the host keeps it)
    private static void Tag(Dictionary<int, int>? mine, Chara chara, int owner, bool copy)
    {
        chara.SetInt(StoryOwnerKey, owner);
        if (copy) {
            chara.SetInt(StoryCopyKey, 1);
        }

        if (mine is not null) {
            mine[owner] = chara.uid;
        }
    }

    // an old world: the story the world's Loytel or Farris already lives with
    private static int TakerOf(Chara world)
    {
        if (world.CompanionOwnerUid is not 0 and var player) {
            return OwnerOf(player);
        }

        return Teams.LandOwnerOf(world.homeZone) is not 0 and var land ? land : WorldOwner;
    }

    // the owner's base: a land of its own, or a player's home
    private static Zone? BaseOf(int owner)
    {
        return Teams.LandsOf(owner).Select(z => EClass.game.spatials.Find(z)).FirstOrDefault(z => z?.branch is not null) ??
               StoryPlayers(owner).Select(p => p.homeBranch?.owner).FirstOrDefault(z => z is not null);
    }

    // a Loytel of that owner's story, where QuestDebt.OnStart puts him: by the player starting the debt, else at the
    // owner's base (a land of its own, or a player's home), else where the game puts him before the debt
    private static Chara MakeLoytel(int owner)
    {
        var copy = CharaGen.Create("loytel");
        copy.SetGlobal();
        Tag(ElinNetHost.StoryLoytels, copy, owner, true);
        // (always the owner's base: QuestDebt.OnStart only knows the local pc, which may be another story's player)
        if (BaseOf(owner) is { } home) {
            copy.MoveHome(home);
            home.branch.AddMemeber(copy);
        } else {
            copy.MoveHome("olvina", 59, 66);
        }

        EmpLog.Information("Story {Owner} gets a Loytel of its own: {Uid} in {Zone}", Name(owner), copy.uid, copy.currentZone?.id);
        return copy;
    }

    /// <summary>
    ///     In the game that keeps the book: that owner's own Farris. The world's Farris (made at the first visit of
    ///     Nymelle's second floor) is the story's that met him (in an old world whose companion he is, whose land he
    ///     lives in, else the world owner's); not met yet, the first story that looks for him while it still needs him.
    ///     With make, an owner with none gets a copy as met (<see cref="MakeFarris" />)
    /// </summary>
    internal static Chara? FarrisOf(int owner, bool make)
    {
        var mine = ElinNetHost.StoryFarrises;
        if (Mapped(mine, "farris", owner) is { } own) {
            return own;
        }

        if (EClass.game.cards.globalCharas.Values.FirstOrDefault(c => c.id == "farris" && c.GetInt(StoryCopyKey) == 0 && !mine.ContainsValue(c.uid)) is
            { } world) {
            int? taker = world.GetInt(StoryOwnerKey) is not 0 and var tagged ? tagged :
                !Unmet(world) ? TakerOf(world) :
                ExplorationOf(owner) <= QuestExploration.Started ? owner : null;
            if (taker is { } took && !mine.ContainsKey(took)) {
                Tag(mine, world, took, false);
                EmpLog.Information("The world's Farris is the story of {Owner}'s", Name(took));
                if (took == owner) {
                    return world;
                }
            }
        }

        return Kept(mine, "farris", owner) ?? (make ? MakeFarris(owner, null) : null);
    }

    // still waiting in Nymelle to be met (Zone_Nymelle.OnBeforeSimulate, DramaOutcome.QuestExploration_MeetFarris)
    private static bool Unmet(Chara farris)
    {
        return farris.currentZone is Zone_Nymelle && farris.HasEditorTag(EditorTag.AINoMove);
    }

    /// <summary>
    ///     A Farris of that owner's story, as the game makes him: on Nymelle's second floor (the one this game plays)
    ///     by its locked stairs, waiting; without a floor, as met: at home in the world's start
    ///     (QuestExploration_MeetFarris), in the owner's base once recruited (_MeetFarris2)
    /// </summary>
    internal static Chara MakeFarris(int owner, Zone? floor)
    {
        var copy = Patches.NymelleFarrisRepair.Create(0);
        Tag(KeepsBook ? ElinNetHost.StoryFarrises : null, copy, owner, true);
        if (floor is not null) {
            var at = Patches.NymelleFarrisRepair.Spot().GetNearestPoint(allowBlock: false, allowChara: false);
            floor.AddCard(copy, at.x, at.z);
        } else {
            copy.RemoveEditorTag(EditorTag.AINoMove);
            copy.RemoveEditorTag(EditorTag.Invulnerable);
            if (ExplorationOf(owner) >= QuestExploration.AfterMeetFarrisAtHome && BaseOf(owner) is { } home) {
                copy.MoveHome(home);
                home.branch.AddMemeber(copy);
            } else if (EClass.game.StartZone is { } start) {
                copy.MoveHome(start);
            }
        }

        EmpLog.Information("Story {Owner} gets a Farris of its own: {Uid} in {Zone}", Name(owner), copy.uid, copy.currentZone?.id);
        return copy;
    }

    /// <summary>
    ///     Zone_Nymelle.OnBeforeSimulate on the second floor, in the game that plays it (the host, or a player holding
    ///     the floor): the story played here meets its own Farris. The host's other stories on the floor get theirs in
    ///     <see cref="CatchUp()" />; a guest holding the floor tells the host of its own (<see cref="NymelleFarrisDelta" />)
    /// </summary>
    internal static void FarrisOnFloor(Zone_Nymelle floor)
    {
        var session = NetSession.Instance;
        if ((session.Transport is ElinNetClient && !session.IsZoneAuthority) || ViewerOwner is not { } owner) {
            return;
        }

        if (KeepsBook) {
            if (FarrisOf(owner, false) is null && EClass.game.quests.GetPhase<QuestExploration>() <= QuestExploration.Started) {
                MakeFarris(owner, floor);
            }

            return;
        }

        var farris = OwnCopyHere("farris", owner, EClass.game.cards.globalCharas.Values);
        if (farris is null) {
            if (EClass.game.quests.GetPhase<QuestExploration>() > QuestExploration.Started) {
                return;
            }

            farris = MakeFarris(owner, floor);
        }

        if (session.Transport is ElinNetClient main && farris.currentZone == floor) {
            main.SendWhileAway(new NymelleFarrisDelta {
                ZoneUid = floor.uid,
                Uid = farris.uid,
                X = farris.pos.x,
                Z = farris.pos.z,
                Owner = owner,
            });
        }
    }

    /// <summary>
    ///     The host, told by a guest holding the floor of the Farris its story has there: may it be that owner's? One
    ///     the host had for that owner, still waiting in Nymelle, gives way (the guest is the one who talks to its
    ///     own); not when that owner already met another one
    /// </summary>
    internal static bool FarrisToldBy(int owner, int uid)
    {
        var mine = ElinNetHost.StoryFarrises;
        if ((Mapped(mine, "farris", owner) ?? Kept(mine, "farris", owner)) is not { } known) {
            return true;
        }

        // (not one this game shows right now: a teammate of the host's on the same floor)
        if (known.uid == uid || !Unmet(known) || known.currentZone == EClass._zone) {
            return false;
        }

        mine.Remove(owner);
        if (known.GetInt(StoryCopyKey) == 1) {
            known.Destroy();
        } else {
            known.SetInt(StoryOwnerKey, 0);
        }

        EmpLog.Information("Story {Owner}'s Farris {Old} gives way to {Uid}, met in its player's game", Name(owner), known.uid, uid);
        return true;
    }

    internal static void TagFarris(Chara farris, int owner)
    {
        Tag(ElinNetHost.StoryFarrises, farris, owner, true);
    }

    // ---- the guilds, each owner's

    /// <summary>
    ///     The guilds box changed while the stories are apart. Ticked: the guilds were everyone's until now, every
    ///     story starts with the world's. Unticked: the world's guilds are the world owner's again (the save and box
    ///     off rule), every player is told them
    /// </summary>
    private static void GuildsFlip()
    {
        var want = Guilds ? 1 : 0;
        if (State[4] == want || NetSession.Instance.Transport is ElinNetHost { IsZoneSession: true }) {
            return;
        }

        State[4] = want;
        if (want == 1) {
            var world = Take(true);
            foreach (var owner in ElinNetHost.TeamStoryBundles.Keys.ToArray()) {
                if (owner != LocalOwner && Stored(owner) is { } bundle) {
                    Store(owner, WithGuilds(bundle, world));
                }
            }

            EmpLog.Information("Guilds per player or team: every story starts with the world's guilds");
            return;
        }

        var kept = WorldOwner == LocalOwner ? Take(true) : Stored(WorldOwner) is { Guilds: not null } stored ? stored : Take(true);
        foreach (var owner in ElinNetHost.TeamStoryBundles.Keys.ToArray()) {
            if (Stored(owner) is { Guilds: not null } bundle) {
                bundle.Guilds = null;
                bundle.Quests = LZ4Bytes.Create(Read(bundle.Quests).Where(IsStory).ToList()).Bytes;
                Store(owner, bundle);
            }
        }

        if (WorldOwner != LocalOwner) {
            Put(WithGuilds(Take(false), kept));
            Rebaseline(true);
        }

        if (NetSession.Instance.Transport is ElinNetHost host) {
            foreach (var (peer, uid) in host.PeerCharas()) {
                host.SendDeltaTo(peer, new StoryBundleDelta { Bundle = WithGuilds(Of(OwnerOf(uid)), kept) });
            }
        }

        EmpLog.Information("The guilds are the world's again: the world owner's ({Owner})", Name(WorldOwner));
    }

    // that story with the guilds of another (its places and its guild quests)
    private static StoryBundle WithGuilds(StoryBundle story, StoryBundle guilds)
    {
        story.Guilds = guilds.Guilds is null ? null : new(guilds.Guilds);
        story.Quests = LZ4Bytes.Create(Read(story.Quests).Where(IsStory).Concat(Read(guilds.Quests).OfType<QuestGuild>()).ToList()).Bytes;
        return story;
    }

    // ---- what the story gives, once per player

    // the step of the story being played now ("quest id:phase"), none outside one
    private static string? _step;

    /// <summary>
    ///     The step of a story quest the game plays now, while the box is on: what it gives is had once per player,
    ///     whatever story that player plays it in (team hopping farms nothing)
    /// </summary>
    internal static string? StepKey => KeepsBook && On && Seeded ? _step : null;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Quest), nameof(Quest.ChangePhase))]
    private static void BeforeStep(Quest __instance, int a, out string? __state)
    {
        __state = _step;
        // (the debt's rewards come with its bills, each owner pays its own: not a step)
        if (__instance is QuestMain or QuestExploration) {
            _step = $"{__instance.id}:{a}";
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Quest), nameof(Quest.ChangePhase))]
    private static void AfterStep(string? __state)
    {
        _step = __state;
    }

    // (what a dialog of the exploration gives: its scrolls)
    [HarmonyPatch]
    private static class ExplorationOutcomes
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            return typeof(DramaOutcome).GetMethods().Where(m => m.DeclaringType == typeof(DramaOutcome) && m.Name.StartsWith("QuestExploration_"));
        }

        [HarmonyPrefix]
        private static void Begin(System.Reflection.MethodBase __originalMethod, out string? __state)
        {
            __state = _step;
            _step = $"exploration:{EClass.game.quests.GetPhase<QuestExploration>()}:{__originalMethod.Name}";
        }

        [HarmonyFinalizer]
        private static void End(string? __state)
        {
            _step = __state;
        }
    }

    internal static bool Had(int charaUid, string? step)
    {
        return step is not null && ElinNetHost.TeamStoryHad.TryGetValue(charaUid, out var had) && had.Contains(step);
    }

    internal static void NoteHad(int charaUid, string? step)
    {
        if (step is null || Had(charaUid, step)) {
            return;
        }

        ElinNetHost.TeamStoryHad[charaUid] = [..ElinNetHost.TeamStoryHad.GetValueOrDefault(charaUid) ?? [], step];
    }

    private static Thing? _withheld;

    /// <summary>
    ///     Player.DropReward, in the game that keeps the book: a reward of a story step its earner already had (in
    ///     another story, or as a share) is not given again. True when withheld
    /// </summary>
    internal static bool Withhold(Thing reward, Chara? earner)
    {
        if (StepKey is not { } step || !Patches.StoryScope.Gives(reward)) {
            return false;
        }

        var uid = earner?.uid ?? ServerAvatar.LocalUid();
        if (!Had(uid, step)) {
            NoteHad(uid, step);
            return false;
        }

        _withheld = reward;
        EmpLog.Information("Story reward {Id} of step {Step}: player {Uid} had it already", reward.id, step, uid);
        return true;
    }

    internal static bool Withheld(Thing? reward)
    {
        return reward is not null && ReferenceEquals(reward, _withheld);
    }

    // ---- the save: the world owner's story, then the local team's again

    /// <summary>
    ///     The game writes its save: the quest log holds the world owner's story while it is written. A save opened
    ///     without the mod, or with the box off, goes on with it
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void BeforeSave(out Snapshot? __state)
    {
        __state = null;
        if (LocalOwner is not { } team || !On || EClass.game?.quests is null) {
            return;
        }

        // (the ledger is written with this save: the local team's as it is now)
        Store(team, Take());
        if (team == WorldOwner || Stored(WorldOwner) is not { } world) {
            return;
        }

        __state = Snap();
        Put(world);
    }

    // a finalizer: a save that throws does not leave the world owner's story in place
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void AfterSave(Snapshot? __state)
    {
        if (__state is not null) {
            Restore(__state);
        }
    }

    // ---- the quest log

    /// <summary>
    ///     A copy of the story the local quest log holds (its guilds: while they are each owner's, or when asked)
    /// </summary>
    internal static StoryBundle Take(bool? guilds = null)
    {
        var quests = EClass.game.quests;
        var withGuilds = guilds ?? Guilds;
        return TakeValues(new() {
            Quests = LZ4Bytes.Create(quests.list.Where(q => InLog(q, withGuilds)).ToList()).Bytes,
            Offers = LZ4Bytes.Create(quests.globalList.Where(IsStory).ToList()).Bytes,
            Main = quests.GetPhase<QuestMain>(),
            Exploration = quests.GetPhase<QuestExploration>(),
        }, withGuilds);
    }

    // the plain values of the story the local game holds, besides its quests: the story quests it finished, the dialog
    // choices, the key items, the debt and the extra tax, the story's flags, the guilds when asked
    private static StoryBundle TakeValues(StoryBundle bundle, bool guilds = false)
    {
        var quests = EClass.game.quests;
        var player = EClass.player;
        bundle.Completed = [..quests.completedIDs.Where(StoryIds.Contains), ..quests.completedTypes.Where(StoryTypes.Contains)];
        bundle.Dialog = new(player.dialogFlags);
        bundle.AllDialog = true;
        bundle.KeyItems = new(player.keyItems);
        bundle.Debt = player.debt;
        bundle.ExtraTax = player.extraTax;
        bundle.Flags = StoryFlagProps.ToDictionary(p => p.Name, p => p.GetValue(player.flags) switch {
            bool set => set ? 1 : 0,
            int number => number,
            _ => 0,
        });
        bundle.Guilds = guilds
            ? GuildFactions().ToDictionary(g => g.id, g => new[] { (int)g.relation.type, g.relation.rank, g.relation.exp })
            : null;
        return bundle;
    }

    // (a value a bundle does not carry, written by an older version: what the game holds stays, see Upgrade)
    private static void PutValues(StoryBundle bundle)
    {
        var quests = EClass.game.quests;
        var player = EClass.player;
        quests.completedIDs.ExceptWith(StoryIds);
        quests.completedTypes.ExceptWith(StoryTypes);
        foreach (var done in bundle.Completed) {
            (StoryTypes.Contains(done) ? quests.completedTypes : quests.completedIDs).Add(done);
        }

        if (bundle.AllDialog) {
            player.dialogFlags.Clear();
        }

        foreach (var id in bundle.AllDialog ? bundle.Dialog.Keys : (IEnumerable<string>)DialogIds) {
            if (bundle.Dialog.TryGetValue(id, out var value)) {
                player.dialogFlags[id] = value;
            } else {
                player.dialogFlags.Remove(id);
            }
        }

        if (bundle.KeyItems is { } items) {
            player.keyItems.Clear();
            foreach (var (item, count) in items) {
                player.keyItems[item] = count;
            }
        }

        if (bundle.Debt is { } debt) {
            player.debt = debt;
        }

        if (bundle.ExtraTax is { } extraTax) {
            player.extraTax = extraTax;
        }

        foreach (var flag in StoryFlagProps) {
            if (bundle.Flags?.TryGetValue(flag.Name, out var value) == true) {
                flag.SetValue(player.flags, flag.PropertyType == typeof(bool) ? value != 0 : value);
            }
        }

        foreach (var guild in GuildFactions()) {
            if (bundle.Guilds?.TryGetValue(guild.id, out var place) == true && place.Length == 3) {
                guild.relation.type = (FactionRelation.RelationType)place[0];
                guild.relation.rank = place[1];
                guild.relation.exp = place[2];
            }
        }
    }

    // the four guilds of the world (DialogFlagSync's)
    private static IEnumerable<Faction> GuildFactions()
    {
        var factions = EClass.game.factions;
        return new Faction?[] { factions.Fighter, factions.Mage, factions.Thief, factions.Merchant }.OfType<Faction>()
            .Where(g => g.relation is not null);
    }

    /// <summary>
    ///     The local quest log holds this story, and no other (new objects: what was there is dropped)
    /// </summary>
    internal static void Put(StoryBundle bundle)
    {
        var quests = EClass.game.quests;
        var guilds = bundle.Guilds is not null;
        quests.list.RemoveAll(q => InLog(q, guilds));
        quests.globalList.RemoveAll(IsStory);

        // (the game's own fix after a load, for one log: a story quest once)
        foreach (var quest in Read(bundle.Quests).Where(q => InLog(q, guilds)).GroupBy(q => q.GetType()).Select(g => g.First()).Reverse()) {
            quest.task?.SetOwner(quest);
            quests.list.Insert(0, quest);
        }

        foreach (var offer in Read(bundle.Offers).Where(IsStory).GroupBy(q => q.GetType()).Select(g => g.First())) {
            quests.globalList.Add(offer);
        }

        PutValues(bundle);
    }

    // (the story quests and the guild quests a bundle holds)
    private static List<Quest> Read(byte[] data)
    {
        if (data.Length == 0) {
            return [];
        }

        try {
            return new LZ4Bytes { Bytes = data }.Decompress<List<Quest>>()?.Where(q => InLog(q, true)).ToList() ?? [];
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "Story quests of a team could not be read");
            return [];
        }
    }

    /// <summary>
    ///     A ledger written before a value was part of the story (a save of an older version): each story gets what the
    ///     world holds now, read before the host takes its own (the save's values)
    /// </summary>
    private static void Upgrade()
    {
        StoryBundle? world = null;
        foreach (var owner in ElinNetHost.TeamStoryBundles.Keys.ToArray()) {
            if (Stored(owner) is not { } bundle ||
                (bundle.AllDialog && bundle.KeyItems is not null && bundle.Debt is not null && bundle.ExtraTax is not null &&
                 StoryFlagProps.All(p => bundle.Flags?.ContainsKey(p.Name) == true))) {
                continue;
            }

            world ??= TakeValues(new());
            if (bundle.Debt is null) {
                // (the debt quest was the world's then: each story gets the world's)
                var quests = EClass.game.quests;
                bundle.Quests = LZ4Bytes.Create(Read(bundle.Quests).Concat(quests.list.OfType<QuestDebt>()).ToList<Quest>()).Bytes;
                bundle.Offers = LZ4Bytes.Create(Read(bundle.Offers).Concat(quests.globalList.OfType<QuestDebt>()).ToList<Quest>()).Bytes;
                bundle.Completed = [..bundle.Completed, ..world.Completed.Where(done => done is "debt" or nameof(QuestDebt))];
            }

            if (!bundle.AllDialog) {
                // (the other dialogs were the world's then: its own choices with Ash and Fiama over the world's)
                var dialog = world.Dialog.Where(d => !DialogIds.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value);
                foreach (var (id, value) in bundle.Dialog) {
                    dialog[id] = value;
                }

                bundle.Dialog = dialog;
                bundle.AllDialog = true;
            }

            bundle.KeyItems ??= world.KeyItems;
            bundle.Debt ??= world.Debt;
            bundle.ExtraTax ??= world.ExtraTax;
            bundle.Flags ??= [];
            foreach (var (name, value) in world.Flags!) {
                bundle.Flags.TryAdd(name, value);
            }

            Store(owner, bundle);
            EmpLog.Information("The story of {Owner} was written by an older version: it gets the world's values it lacks", Name(owner));
        }
    }

    /// <summary>
    ///     The story objects of the local quest log, the exact ones, to put back as they were, and its plain values
    /// </summary>
    internal sealed class Snapshot
    {
        internal required List<(int Index, Quest Quest)> List { get; init; }
        internal required List<(int Index, Quest Quest)> Offers { get; init; }
        internal required StoryBundle Values { get; init; }
    }

    internal static Snapshot Snap()
    {
        var quests = EClass.game.quests;
        var guilds = Guilds;
        return new() {
            List = quests.list.Select((q, i) => (i, q)).Where(x => InLog(x.q, guilds)).ToList(),
            Offers = quests.globalList.Select((q, i) => (i, q)).Where(x => IsStory(x.q)).ToList(),
            Values = TakeValues(new(), guilds),
        };
    }

    internal static void Restore(Snapshot snap)
    {
        var quests = EClass.game.quests;
        var guilds = snap.Values.Guilds is not null;
        quests.list.RemoveAll(q => InLog(q, guilds));
        quests.globalList.RemoveAll(IsStory);
        foreach (var (index, quest) in snap.List) {
            quests.list.Insert(System.Math.Min(index, quests.list.Count), quest);
        }

        foreach (var (index, quest) in snap.Offers) {
            quests.globalList.Insert(System.Math.Min(index, quests.globalList.Count), quest);
        }

        PutValues(snap.Values);
    }

    // ---- the ledger

    /// <summary>
    ///     That team's story in the ledger, none when it has no entry (or one that cannot be read)
    /// </summary>
    internal static StoryBundle? Stored(int team)
    {
        if (!ElinNetHost.TeamStoryBundles.TryGetValue(team, out var text)) {
            return null;
        }

        try {
            return JsonConvert.DeserializeObject<StoryBundle>(text);
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "The story of team {TeamId} could not be read", team);
            return null;
        }
    }

    internal static void Store(int team, StoryBundle bundle)
    {
        ElinNetHost.TeamStoryBundles[team] = JsonConvert.SerializeObject(bundle);
    }

    /// <summary>
    ///     What the bench reads: each team's main and exploration phases, the world owner
    /// </summary>
    internal static string Describe()
    {
        var lines = ElinNetHost.TeamStoryBundles.Keys.OrderBy(k => k).Select(team => Stored(team) is { } bundle
            ? $"{Name(team)} main {bundle.Main} exploration {bundle.Exploration} dialog {string.Join(",", bundle.Dialog.Where(d => DialogIds.Contains(d.Key)).Select(d => $"{d.Key}={d.Value}"))}"
            : $"{Name(team)} unreadable");
        return $"schema {State[0]} seeded {State[1]} world {State[2]} lothria {State[3]} local {LocalOwner?.ToString() ?? "-"}\n" +
               string.Join("\n", lines);
    }

    private static string Name(int owner) => StoryOwner.Name(owner);

    /// <summary>
    ///     The host's explicit choice: the story of that owner (a team id, or minus a chara uid) is the one the save keeps
    ///     from now on
    /// </summary>
    internal static string SetWorldOwner(int team)
    {
        if (!KeepsBook || !On || !Seeded) {
            return "Host only, with the box on";
        }

        if (team == SeedOwner || (Stored(team) is null && team != LocalOwner)) {
            return $"No story for {team} in the ledger";
        }

        State[2] = team;
        EmpLog.Information("The world owner is now {TeamId}: the save keeps its story", team);
        return $"The world owner is {team}";
    }
}
