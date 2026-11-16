using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Patches;

namespace ElinTogether.Net;

/// <summary>
///     Council 14, "a share for everyone": the story is the group's (one quest log, one set of story flags), so
///     what a story quest or a dialog gives is given to every player of the world, not only to the one who
///     completed the quest or played the dialog. Nobody plays anything again, nobody is asked anything. <br />
///     One place: the game's own <c>Player.DropReward</c>, while a story quest completes or a dialog outcome
///     runs (<see cref="StoryScope" />), in the game that hosts the world. A player on the map finds its share
///     in its bag; a player elsewhere or not connected gets a parcel in its name in the mail of the base. <br />
///     Only goods a player carries are multiplied. What is the world's is not: recipes (known by all already),
///     deeds, and artifacts, except the few named here. What is not listed is not multiplied
/// </summary>
// What a world gave BEFORE the ledger existed: see SeedOldWorld (council 15)
internal partial class ElinNetHost
{
    /// <summary>
    ///     The ledger of the world: each reward of the story that was shared, as given (number -> the thing)
    /// </summary>
    [ElinGameIOProperty("story_rewards")]
    private static Dictionary<int, byte[]> StoryRewards
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Reward number -> the characters of the players who have it: who earned it, who got a share
    /// </summary>
    [ElinGameIOProperty("story_received")]
    private static Dictionary<int, int[]> StoryReceived
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Reward number -> the story step that gave it ("quest id:phase"), when each player or team lives its own
    ///     story (TeamOwnStory): the same step played again in another story adds nothing to the ledger, and a player
    ///     who had that step's reward does not get it again
    /// </summary>
    [ElinGameIOProperty("story_reward_steps")]
    private static Dictionary<int, string> StoryRewardSteps
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Set once the rewards a world gave before the ledger existed were written in it, see SeedOldWorld
    /// </summary>
    [ElinGameIOProperty("story_seeded")]
    private static int[] StorySeeded
    {
        get => field is { Length: 1 } ? field : field = new int[1];
        set;
    }

    // (the goods only: recipes are known by all, the tickets of the debt depend on a stage nothing records,
    // the gifts of the wedding are made from one person, the gifts of dialogs left no trace of having been given)
    private static readonly (string Quest, System.Func<Thing[]> Rewards)[] _oldRewards = [
        ("QuestCrafter", () => [ThingGen.Create("housePlate"), ThingGen.Create("343"), ThingGen.Create("432")]),
        ("QuestDefense", () => [ThingGen.Create("plat").SetNum(10)]),
        ("QuestPuppy", () => [ThingGen.Create("coolerbox")]),
        ("QuestCompanion", () => [ThingGen.CreateScroll(9001)]),
        ("QuestTax", () => [ThingGen.Create("mailpost")]),
        ("QuestCurry", () => [TraitSeed.MakeSeed("redpepper").SetNum(3)]),
        ("QuestLoytelFarm", () => [
            TraitSeed.MakeSeed("pasture").SetNum(5), TraitSeed.MakeSeed("tomato").SetNum(5),
            TraitSeed.MakeSeed("kinoko").SetNum(5),
        ]),
        ("QuestNasu", () => [
            ThingGen.Create("697").SetNum(77), TraitSeed.MakeSeed("feywood").SetNum(10),
            TraitSeed.MakeSeed("coralwood").SetNum(10),
        ]),
        ("ash1", () => [ThingGen.Create("crimAle")]),
        ("fiama1", () => [ThingGen.Create("crimAle")]),
    ];

    /// <summary>
    ///     Council 15: a world played before the ledger existed has story quests completed and nothing that says
    ///     who had their rewards. They are written in the ledger as received by nobody: every player of the
    ///     world, the host as the others, gets them once, in one parcel in its name (a spare of what it already
    ///     had costs little between friends, a reward never had cannot be caught up). Once per world. In a world
    ///     only its host ever played, the host is known to have had them: only who joins later gets them
    /// </summary>
    internal static void SeedOldWorld()
    {
        if (StorySeeded[0] != 0 || !EmpConfig.Server.OncePerPlayer.Value || NetSession.Instance.Transport is not ElinNetHost ||
            !core.IsGameStarted || game?.quests is not { } quests) {
            return;
        }

        StorySeeded[0] = 1;

        // a world nobody else ever played in: its one player had them all
        int[] had = SavedRemoteCharas.Count == 0 ? [player.uidChara] : [];
        var added = 0;
        foreach (var (quest, rewards) in _oldRewards) {
            if (!quests.completedTypes.Contains(quest) && !quests.completedIDs.Contains(quest)) {
                continue;
            }

            try {
                foreach (var reward in rewards()) {
                    var number = StoryRewards.Count == 0 ? 1 : StoryRewards.Keys.Max() + 1;
                    StoryRewards[number] = LZ4Bytes.Create(reward).Bytes;
                    StoryReceived[number] = had;
                    reward.Destroy();
                    added++;
                }
            } catch (System.Exception ex) {
                EmpLog.Warning(ex, "Rewards of {Quest} could not be made for the ledger", quest);
            }
        }

        if (added == 0) {
            return;
        }

        EmpLog.Information("World played before the ledger: {Count} story rewards written for every player", added);
        if (pc is { isDead: false } own) {
            CatchUpStoryShares(own);
        }
    }

    /// <summary>
    ///     Set once the story's own artifacts a world gave before they were shared were written in the ledger, see
    ///     SeedOwnArtifacts
    /// </summary>
    [ElinGameIOProperty("story_artifacts_seeded")]
    private static int[] StoryArtifactsSeeded
    {
        get => field is { Length: 1 } ? field : field = new int[1];
        set;
    }

    // the story's artifacts a world may have given before they were shared, and the quest that gives each (#40:
    // Poppy's coolerbox was an artifact, never shared: one player of the world had it; see _ownArtifacts)
    private static readonly (string Quest, string Id)[] _sharedArtifacts = [("QuestPuppy", "coolerbox")];

    /// <summary>
    ///     A world that completed such a quest before its artifact was shared has it in nobody's share: written in the
    ///     ledger once, received by nobody (the precedent of SeedOldWorld: a spare for whoever had it costs little), so
    ///     every player gets one in a parcel at its next arrival. Once per world
    /// </summary>
    internal static void SeedOwnArtifacts()
    {
        if (StoryArtifactsSeeded[0] != 0 || !EmpConfig.Server.OncePerPlayer.Value || NetSession.Instance.Transport is not ElinNetHost ||
            !core.IsGameStarted || game?.quests is not { } quests || StorySeeded[0] == 0) {
            return;
        }

        StoryArtifactsSeeded[0] = 1;
        var held = new HashSet<string>();
        foreach (var data in StoryRewards.Values) {
            try {
                held.Add(new LZ4Bytes { Bytes = data }.Decompress<Thing>().id);
            } catch (System.Exception) {
                // (an entry that cannot be read is no proof of anything)
            }
        }

        // a world nobody else ever played in: its one player had them
        int[] had = SavedRemoteCharas.Count == 0 ? [player.uidChara] : [];
        foreach (var (quest, id) in _sharedArtifacts) {
            if (held.Contains(id) || (!quests.completedTypes.Contains(quest) && !quests.completedIDs.Contains(quest))) {
                continue;
            }

            var reward = ThingGen.Create(id);
            var number = StoryRewards.Count == 0 ? 1 : StoryRewards.Keys.Max() + 1;
            StoryRewards[number] = LZ4Bytes.Create(reward).Bytes;
            StoryReceived[number] = had;
            reward.Destroy();
            EmpLog.Information("Story artifact {Id} of {Quest}, given before it was shared: in the ledger for every player", id, quest);
        }
    }

    /// <summary>
    ///     A player stands on the host's map: what the story gave before it played in this world, or while
    ///     nothing knew it, comes to it as one parcel in its name in the mail of the base (not a flood in its
    ///     bag). Each reward once: the ledger notes it
    /// </summary>
    internal static void CatchUpStoryShares(Chara player)
    {
        if (!EmpConfig.Server.OncePerPlayer.Value || StoryRewards.Count == 0 || ServerAvatar.Is(player)) {
            return;
        }

        var owed = new List<Thing>();
        foreach (var (number, data) in StoryRewards.ToArray()) {
            var received = StoryReceived.GetValueOrDefault(number) ?? [];
            if (received.Contains(player.uid)) {
                continue;
            }

            // (that step's reward had in another story)
            var step = StoryRewardSteps.GetValueOrDefault(number);
            if (TeamOwnStory.Had(player.uid, step)) {
                StoryReceived[number] = received.Append(player.uid).ToArray();
                continue;
            }

            TeamOwnStory.NoteHad(player.uid, step);

            try {
                var share = new LZ4Bytes { Bytes = data }.Decompress<Thing>();
                game.cards.AssignUIDRecursive(share);
                owed.Add(share);
            } catch (System.Exception ex) {
                EmpLog.Warning(ex, "Story reward {Number} of the ledger could not be read", number);
            }

            // (noted even when it could not be read: not tried again at every arrival)
            StoryReceived[number] = received.Append(player.uid).ToArray();
        }

        if (owed.Count == 0) {
            return;
        }

        var parcel = ThingGen.CreateParcel(null, owed.ToArray());
        parcel.c_idRefName = "emp_story_share".Loc(player.NameSimple);
        TeamMail.Send(player, parcel);
        EmpLog.Information("Story rewards given before player {Uid} knew of them: {Count} in a parcel in its name",
            player.uid, owed.Count);
    }

    // rewards dropped this frame and who earned them: copied at the end of the frame, once the story code is
    // done with them (charges, material, what a chest holds are set after the drop)
    private static readonly List<(Thing Reward, int Earner, string? Step)> _storyRewards = [];

    // artifacts every player must have its own of
    private static readonly HashSet<string> _ownArtifacts = ["backpack_holding", "book_story", "coolerbox"];

    internal static void NoteStoryReward(Thing reward, Chara? earner)
    {
        if (reward is not null && StoryScope.Gives(reward)) {
            ShareStoryReward(reward, earner);
        }
    }

    /// <summary>
    ///     A reward of the story known as such by the caller (a gift a client's dialog asked this game to make)
    /// </summary>
    internal static void ShareStoryReward(Thing reward, Chara? earner)
    {
        if (reward is null || NetSession.Instance.Transport is ElinNetClient || !IsSharedWorld ||
            !EmpConfig.Server.OncePerPlayer.Value) {
            return;
        }

        _storyRewards.Add((reward, (earner ?? pc).uid, TeamOwnStory.StepKey));
    }

    // (the game's Duplicate makes a new thing of that kind: a chest comes with loot of its own drawn at random,
    // and without what the reward holds)
    private static Thing Whole(Thing reward)
    {
        var copy = reward.Duplicate(reward.Num);
        copy.things.DestroyAll();
        foreach (var held in reward.things) {
            copy.AddCard(Whole(held));
        }

        return copy;
    }

    /// <summary>
    ///     End of the frame
    /// </summary>
    internal static void GiveStoryShares()
    {
        SeedOldWorld();
        SeedOwnArtifacts();
        if (_storyRewards.Count == 0) {
            return;
        }

        var rewards = _storyRewards.ToArray();
        _storyRewards.Clear();
        if (!core.IsGameStarted || _zone is null) {
            return;
        }

        // every character a player plays in this world, the local one included (a server's stand-in and its retired
        // character are no players: PlayerCharaUids)
        var players = PlayerCharaUids()
            .Select(uid => game.cards.globalCharas.Find(uid))
            .Where(c => c is { isDead: false })
            .ToList();
        var host = NetSession.Instance.Transport as ElinNetHost;

        foreach (var (reward, earner, step) in rewards) {
            if (reward.isDestroyed || !Multiplied(reward)) {
                continue;
            }

            // in the ledger, for who plays in this world later (once per step of the story: another story playing it
            // again adds nothing)
            if (step is null || !StoryRewardSteps.ContainsValue(step)) {
                var number = StoryRewards.Count == 0 ? 1 : StoryRewards.Keys.Max() + 1;
                StoryRewards[number] = LZ4Bytes.Create(Whole(reward)).Bytes;
                StoryReceived[number] = players.Select(p => p!.uid).Append(earner).Distinct().ToArray();
                if (step is not null) {
                    StoryRewardSteps[number] = step;
                }
            }

            foreach (var other in players.Where(p => p!.uid != earner && !TeamOwnStory.Had(p.uid, step))) {
                TeamOwnStory.NoteHad(other!.uid, step);
                var share = Whole(reward);

                // in its bag when it is here: at its feet it piled up with the earner's own, one tile away, and the
                // first to bend down had both (and nothing lands in water or in a wall). A full bag: at its feet
                var here = other!.IsAliveInCurrentZone && _map.charas.Contains(other);
                if (here && !other.things.IsFull(share)) {
                    using (ElinDelta.Simulate()) {
                        other.AddThing(share);
                    }
                } else if (here) {
                    CardCache.Add(share);
                    CardCache.CacheContainer(share.things);
                    using (ElinDelta.Simulate()) {
                        host?.Delta.AddRemote(CardGenDelta.Create(share));
                        _zone.AddCard(share, other.pos);
                    }
                } else {
                    var parcel = ThingGen.CreateParcel(null, share);
                    parcel.c_idRefName = "emp_story_share".Loc(other.NameSimple);
                    TeamMail.Send(other, parcel);
                }

                TellShare(host, other, (here ? "emp_story_share_got" : "emp_story_share_mail").Loc(share.Name));
                EmpLog.Information("Story reward {Id} x{Num}: a share for player {Uid} ({Where})",
                    reward.id, reward.Num, other.uid, here ? "in its bag" : "by mail");
            }
        }
    }

    // ponytail: in the language of the game that hosts; a text id read by each game if players complain
    private static void TellShare(ElinNetHost? host, Chara player, string text)
    {
        if (player.IsPC) {
            Msg.SayRaw(text);
            return;
        }

        if (host?.Socket.Peers.FirstOrDefault(p => SavedRemoteCharas.TryGetValue(p.User, out var uid) && uid == player.uid) is { } peer) {
            host.SendDeltaTo(peer.Id, new MsgSayDelta { Text = text, R = 1f, G = 1f, B = 1f, A = 1f });
        }
    }

    private static bool Multiplied(Thing reward)
    {
        // known by every player already, or the world's (teams learning on their own: a recipe is not known by the
        // other teams, each player gets its own to read)
        var kind = reward.trait.GetType().Name;
        if ((kind.Contains("Recipe") && !TeamKnowledge.On) || kind.Contains("Deed") || kind.Contains("License")) {
            return false;
        }

        return reward.rarity < Rarity.Artifact || _ownArtifacts.Contains(reward.id);
    }
}
