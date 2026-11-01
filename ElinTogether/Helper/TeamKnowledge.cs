using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using Newtonsoft.Json;

namespace ElinTogether.Helper;

/// <summary>
///     Council 2026-10-25, host option "Teams learn on their own" (dev/PLAN_teams_knowledge.md). The recipes, the
///     recipes crafted once and the codex live in the one Player object of the world: with the box on, each OWNER has
///     its own (<see cref="StoryOwner" />, the user 2026-10-28: a team shares one book, a player without a team has its
///     own, never the Wanderers as a group). Off by default, always on for the official public server. The host keeps every team's in its save (<see cref="ElinNetHost.TeamLoreBook" />); every game, the
///     host's own included, holds its team's in that Player object while it plays; the vanilla save gets the union of
///     all teams, so a world opened without the mod, or with the box off, keeps everything anyone learnt
/// </summary>
[HarmonyPatch]
internal static class TeamKnowledge
{
    /// <summary>
    ///     The ledger's layout. A save written by a newer layout is left as it is (one book for everyone)
    /// </summary>
    private const int Schema = 1;

    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules
    /// </summary>
    internal static bool On => KeepsBook ? Wanted : NetSession.Instance.Rules is { TeamKnowledge: true };

    /// <summary>
    ///     The host's box, read as on whatever is stored while this game hosts the official public server
    /// </summary>
    internal static bool Wanted => EmpConfig.Server.TeamKnowledge.Value || StoryOwner.OfficialHosting;

    // the game the ledger was read against, and the team whose knowledge its Player object holds (none: the world's,
    // as the save has it)
    private static Game? _game;
    private static int? _team;
    private static bool? _wasWanted;

    /// <summary>
    ///     The team whose knowledge the local Player object holds, in the game that keeps the book; none while it holds
    ///     the world's
    /// </summary>
    internal static int? LocalTeam => KeepsBook && ReferenceEquals(EClass.game, _game) ? _team : null;

    private static bool Seeded => ElinNetHost.TeamLoreState[1] != 0;

    /// <summary>
    ///     Every frame, in the game that keeps the book: the box ticked gives every team a copy of what the world
    ///     knows (once), the box unticked gives the world back everything any team learnt
    /// </summary>
    internal static void Update()
    {
        if (!KeepsBook || !EClass.core.IsGameStarted || EClass.game?.player?.recipes is null) {
            return;
        }

        if (!ReferenceEquals(EClass.game, _game)) {
            // (a save just read: its Player object knows what every team knew)
            _game = EClass.game;
            _team = null;
            _wasWanted = null;
            if (ElinNetHost.TeamLoreState[0] > Schema) {
                EmpLog.Warning("The teams' knowledge was written by a newer version of the mod ({Schema}): left as it is",
                    ElinNetHost.TeamLoreState[0]);
            }
        }

        if (ElinNetHost.TeamLoreState[0] > Schema) {
            return;
        }

        // (merged back only when the host unticks the box in this game, see TeamOwnStory.Update)
        var flipped = false;
        var unticked = _wasWanted == true && !Wanted;
        _wasWanted = Wanted;
        if (On && !Seeded) {
            Seed();
            flipped = true;
        } else if (!On && Seeded && unticked) {
            MergeBack();
            flipped = true;
        }

        // the host plays its own knowledge like any player, its team's when it is in one
        if (On && Seeded && StoryOwner.Of(ServerAvatar.LocalUid()) is var team && team != _team) {
            Hold(team);
        }

        if (flipped) {
            TellEveryone();
        }
    }

    /// <summary>
    ///     The local Player object holds what that team knows: what it held goes back to its team first
    /// </summary>
    private static void Hold(int team)
    {
        if (_team is { } was) {
            Store(was, Take(EClass.player));
        }

        var lore = Book(team);
        Rebuild(lore);
        Put(EClass.player, lore);
        _team = team;
        EmpLog.Information("The host plays with the knowledge of team {TeamId}: {Recipes} recipes, {Creatures} creatures",
            team, lore.Known.Count, lore.Codex.Count);
    }

    /// <summary>
    ///     What the game itself writes in the recipes as a save is read (Game.OnLoad, older saves): written in the
    ///     world's list, each team gets the same from its own
    /// </summary>
    private static void Rebuild(TeamLore lore)
    {
        // (Game.cs, below 0.23.100: the heavy training dummy got a second recipe id)
        if (lore.Known.TryGetValue("trainingDummy_heavy", out var times) && times != 0 && !lore.Known.ContainsKey("trainingDummy_heavy1")) {
            lore.Known["trainingDummy_heavy1"] = times;
        }
    }

    /// <summary>
    ///     The box was ticked: every team, the Wanderers too, knows what the world knows now
    /// </summary>
    private static void Seed()
    {
        var world = Take(EClass.player);
        // (and the seed entry: a player new to the world knows what the world knew)
        foreach (var team in StoryOwner.All().Append(StoryOwner.Of(ServerAvatar.LocalUid())).Append(StoryOwner.Seed).Distinct()) {
            Store(team, world);
        }

        ElinNetHost.TeamLoreState = [Schema, 1];
        EmpLog.Information("Teams learn on their own: every team was given what the world knows ({Recipes} recipes, {Creatures} creatures)",
            world.Known.Count, world.Codex.Count);
    }

    /// <summary>
    ///     The box was unticked: the world knows everything any team learnt, the ledger is emptied (ticked again, every
    ///     team starts again from the world's)
    /// </summary>
    private static void MergeBack()
    {
        var all = WorldUnion();
        Put(EClass.player, all);
        _team = null;
        ElinNetHost.TeamLoreBook.Clear();
        ElinNetHost.TeamLoreState = [Schema, 0];
        EmpLog.Information("Teams learn together again: the world knows all the teams knew ({Recipes} recipes, {Creatures} creatures)",
            all.Known.Count, all.Codex.Count);
    }

    /// <summary>
    ///     What the local player knows, and every team of the ledger
    /// </summary>
    private static TeamLore WorldUnion()
    {
        return Union(ElinNetHost.TeamLoreBook.Keys.Select(Stored).OfType<TeamLore>().Prepend(Take(EClass.player)));
    }

    /// <summary>
    ///     What that player's team knows, for its copy of the world; none when the box is off (the copy's lists are the
    ///     world's)
    /// </summary>
    internal static TeamLore? For(Chara chara)
    {
        return LocalTeam is null ? null : Of(StoryOwner.Of(chara.uid));
    }

    /// <summary>
    ///     A guest: what its game knows now, to keep in the copy of the world coming in; none when the box is off
    /// </summary>
    internal static TeamLore? Carry()
    {
        return On && EClass.core.IsGameStarted && EClass.game?.player?.recipes is not null ? Take(EClass.player) : null;
    }

    /// <summary>
    ///     The box was ticked or unticked: every player is told what its team knows now (unticked: the world's)
    /// </summary>
    private static void TellEveryone()
    {
        if (NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } host) {
            return;
        }

        foreach (var (peer, chara) in host.ActiveRemoteCharas) {
            host.SendDeltaTo(peer, new TeamLoreDelta { Lore = For(chara) ?? Take(EClass.player) });
        }
    }

    /// <summary>
    ///     What that team knows now: the local Player object's for the team it holds, the ledger's for another
    /// </summary>
    internal static TeamLore Of(int team)
    {
        return team == LocalTeam ? Take(EClass.player) : Book(team);
    }

    /// <summary>
    ///     What the ledger says that owner knows: an owner it never heard of knows what the world knew at the tick (a
    ///     ledger of the time of teams only: what the Wanderers knew)
    /// </summary>
    private static TeamLore Book(int owner)
    {
        return Stored(owner) ?? Stored(StoryOwner.Seed) ?? Stored(Teams.Wanderers) ?? WorldUnion();
    }

    // ---- teams changing

    /// <summary>
    ///     In the game that keeps the book: that player went from one team to another (or to none). The rule of
    ///     <see cref="StoryOwner" />: joining a team, the player knows what the team knows, or the team what the player
    ///     knew when it has no book yet (its first member); leaving for no team, the player keeps a copy of the team's
    ///     book as its own. Every player of the owner it enters is told. (A team that is no more keeps its entry: the
    ///     save's union still holds what it knew)
    /// </summary>
    internal static void Moved(Chara chara, int fromTeam, int toTeam)
    {
        var from = StoryOwner.Of(fromTeam, chara.uid);
        var to = StoryOwner.Of(toTeam, chara.uid);
        if (LocalTeam is not { } local || from == to) {
            return;
        }

        Store(local, Take(EClass.player));
        if (to < 0 && ElinNetHost.TeamLoreBook.ContainsKey(to) && Stored(to) is { } solo) {
            // (leaving or kicked, with a book of its own: both books, as new sets)
            Store(to, Union([solo, Of(from)]));
        } else if (StoryOwner.TakesMovers(to, ElinNetHost.TeamLoreBook.ContainsKey(to))) {
            Store(to, Of(from));
        }

        EmpLog.Information("A player went from {From} to {To}: it knows {Recipes} recipes, {Creatures} creatures",
            StoryOwner.Name(from), StoryOwner.Name(to), Of(to).Known.Count, Of(to).Codex.Count);
        TellTeam(to);
    }

    /// <summary>
    ///     Every player of that team, the host's own game included, knows what the team knows now
    /// </summary>
    private static void TellTeam(int team)
    {
        if (team == LocalTeam && Stored(team) is { } own) {
            Put(EClass.player, own);
        }

        if (NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } host) {
            return;
        }

        foreach (var (peer, chara) in host.ActiveRemoteCharas) {
            if (StoryOwner.Of(chara.uid) == team) {
                host.SendDeltaTo(peer, new TeamLoreDelta { Lore = Of(team) });
            }
        }
    }

    /// <summary>
    ///     In the game that keeps the book: a craft run here for a player of another team (RemoteCraft.AsCrafter) reads
    ///     that team's recipe levels and kill list, not the local team's the Player object holds. Their book goes in for
    ///     the craft and comes out of it (what the craft changed stays in that team's ledger); the returned action puts
    ///     the local team's back. None when nothing needs to change
    /// </summary>
    internal static System.Action? Lend(Chara crafter)
    {
        if (LocalTeam is not { } local || StoryOwner.Of(crafter.uid) is var team && team == local ||
            EClass.game?.player?.recipes is not { } recipes || EClass.player.codex is not { } codex) {
            return null;
        }

        var (known, crafted, creatures) = (recipes.knownRecipes, recipes.craftedRecipes, codex.creatures);
        Put(EClass.player, Book(team));
        return () => {
            Store(team, Take(EClass.player));
            recipes.knownRecipes = known;
            recipes.craftedRecipes = crafted;
            codex.creatures = creatures;
        };
    }

    // (each team's codex as last read from the ledger, kept while its entry stays the same text)
    private static readonly Dictionary<int, (string? Text, CodexManager Codex)> _codexes = [];

    /// <summary>
    ///     In the game that keeps the book: the codex that a blow of that card reads (the weak spot bonus,
    ///     Card.DamageHP), its player's team's; the local Player object's for the local team or no player. Read only
    /// </summary>
    internal static CodexManager CodexOf(Card? origin)
    {
        var local = EClass.player.codex;
        if (LocalTeam is not { } localTeam || PlayerKarma.PlayerBehind(origin) is not { } player ||
            StoryOwner.Of(player.uid) is var team && team == localTeam) {
            return local;
        }

        var text = ElinNetHost.TeamLoreBook.GetValueOrDefault(team);
        if (_codexes.TryGetValue(team, out var cached) && ReferenceEquals(cached.Text, text)) {
            return cached.Codex;
        }

        var codex = new CodexManager {
            creatures = Book(team).Codex.ToDictionary(c => c.Key, c => new CodexCreature { id = c.Key, _ints = Ints(c.Value) }),
        };
        _codexes[team] = (text, codex);
        return codex;
    }

    // ---- learning

    /// <summary>
    ///     On the host, for a recipe or a codex change a guest relays for a player of the map it keeps: the player it
    ///     names decides whose team's book it goes in, so it must be a player of this world (or the sender itself)
    /// </summary>
    internal static string? CheckLearner(int learner, Chara? sender)
    {
        return learner == 0 || learner == sender?.uid ||
               EClass.game.cards.globalCharas.Find(learner) is { } chara && (chara.IsPC || chara.GetBool("remote_chara"))
            ? null
            : "the learner it names is no player";
    }

    /// <summary>
    ///     What that player learnt, the other learns: same team, or the box off. Learner 0: the world (the story),
    ///     every team
    /// </summary>
    internal static bool Shares(int learner, int other)
    {
        return learner == 0 || !On || StoryOwner.Of(learner) == StoryOwner.Of(other);
    }

    /// <summary>
    ///     The players of that game (the host of the world, or a zone a player keeps) who learn what that player
    ///     learnt, but the one it came from; <paramref name="mapOnly" />: not those away from that map (they get it
    ///     with their next copy of the world). The box off: everyone, as before
    /// </summary>
    internal static void Tell(ElinNetHost host, ElinDelta delta, int learner, int except, bool mapOnly = false)
    {
        if (!mapOnly && (!On || learner == 0)) {
            host.SendDeltaToAllExcept(except, delta);
            return;
        }

        foreach (var (peer, chara) in host.ActiveRemoteCharas) {
            if (peer != except && (!mapOnly || !host.IsAwayPeer(peer)) && Shares(learner, chara.uid)) {
                host.SendDeltaTo(peer, delta);
            }
        }
    }

    /// <summary>
    ///     In the game that keeps the book: a recipe that player learnt is in its team's ledger (learner 0: every
    ///     team's). Not the local team's: its Player object learns it the game's way
    /// </summary>
    internal static void Learnt(int learner, string recipeId)
    {
        if (LocalTeam is not { } local || string.IsNullOrEmpty(recipeId)) {
            return;
        }

        var of = learner == 0 ? (int?)null : StoryOwner.Of(learner);
        // (a player the ledger has no book for yet: its own starts now)
        if (of is { } first && first != local && !ElinNetHost.TeamLoreBook.ContainsKey(first)) {
            Store(first, Book(first));
        }

        foreach (var team in ElinNetHost.TeamLoreBook.Keys.ToArray()) {
            if (team == local || (of is { } only && team != only)) {
                continue;
            }

            var lore = Book(team);
            AddKnown(lore.Known, recipeId);
            Store(team, lore);
        }
    }

    /// <summary>
    ///     In the game that keeps the book: a change of the codex that player made is in its team's ledger (learner 0:
    ///     every team's). Not the local team's: its Player object has it the game's way
    /// </summary>
    internal static void Noted(int learner, CodexKind kind, string id, int num)
    {
        if (LocalTeam is not { } local || string.IsNullOrEmpty(id)) {
            return;
        }

        var of = learner == 0 ? (int?)null : StoryOwner.Of(learner);
        // (a player the ledger has no book for yet: its own starts now)
        if (of is { } first && first != local && !ElinNetHost.TeamLoreBook.ContainsKey(first)) {
            Store(first, Book(first));
        }

        foreach (var team in ElinNetHost.TeamLoreBook.Keys.ToArray()) {
            if (team == local || (of is { } only && team != only)) {
                continue;
            }

            var lore = Book(team);
            var creature = new CodexCreature { id = id, _ints = Ints(lore.Codex.GetValueOrDefault(id)) };
            if (CodexDelta.Change(creature, kind, num)) {
                lore.Codex[id] = creature._ints;
                Store(team, lore);
            }
        }
    }

    /// <summary>
    ///     A recipe scroll another player reads is read twice: in its own game, which learns it and tells this one
    ///     whose team it is, and in the game that runs its map, where it was "the player" (this game's team) who
    ///     learnt it. There it only uses the scroll up
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitRecipe), nameof(TraitRecipe.OnRead))]
    private static bool OnReadRecipe(TraitRecipe __instance, Chara c)
    {
        return ReadHere(__instance, c);
    }

    // (a random recipe of a kind: the reader's own game draws it)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitRecipeCat), nameof(TraitRecipeCat.OnRead))]
    private static bool OnReadRecipeCat(TraitRecipeCat __instance, Chara c)
    {
        return ReadHere(__instance, c);
    }

    private static bool ReadHere(Trait scroll, Chara reader)
    {
        if (!On || reader.IsPC || !reader.IsRemotePlayer || NetSession.Instance.Connection is not ElinNetHost) {
            return true;
        }

        scroll.owner.ModNum(-1);
        return false;
    }

    // RecipeManager.Add without the messages: once more, and the variants of a block learnt with it (pillar, bridge)
    private static void AddKnown(Dictionary<string, int> known, string id)
    {
        if (RecipeManager.Get(id) is not { } source) {
            return;
        }

        known[id] = known.GetValueOrDefault(id) + 1;
        string[] variants = source.row?.category == "floor"
            ? [id + "-p", id.Replace("-p", ""), id + "-b", id.Replace("-b", "")]
            : [id + "-p", id.Replace("-p", "")];
        foreach (var variant in variants) {
            if (variant != id && !known.ContainsKey(variant) && RecipeManager.Get(variant) is not null) {
                AddKnown(known, variant);
            }
        }
    }

    /// <summary>
    ///     In the game that keeps the book: that player crafted that recipe for the first time, its team gets no
    ///     first-time bonus for it again (the local team's Player object, another team's ledger)
    /// </summary>
    internal static void Crafted(int crafter, string recipeId)
    {
        if (LocalTeam is not { } local) {
            return;
        }

        var team = StoryOwner.Of(crafter);
        if (team == local) {
            EClass.player.recipes.craftedRecipes.Add(recipeId);
            return;
        }

        var lore = Book(team);
        if (!lore.Crafted.Contains(recipeId)) {
            lore.Crafted.Add(recipeId);
            Store(team, lore);
        }
    }

    // ---- the save: the union, then the local team's again

    /// <summary>
    ///     The game writes its save: the Player object holds what every team knows while it is written. A save opened
    ///     without the mod, or with the box off, keeps everything
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void BeforeSave(out (Dictionary<string, int>, HashSet<string>, Dictionary<string, CodexCreature>)? __state)
    {
        __state = null;
        if (LocalTeam is not { } team || EClass.game?.player?.recipes is not { } recipes || EClass.player.codex is not { } codex) {
            return;
        }

        // (the ledger is written with this save: the local team's as it is now)
        Store(team, Take(EClass.player));
        __state = (recipes.knownRecipes, recipes.craftedRecipes, codex.creatures);
        Put(EClass.player, WorldUnion());
    }

    // a finalizer: a save that throws does not leave the union in place
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void AfterSave((Dictionary<string, int> Known, HashSet<string> Crafted, Dictionary<string, CodexCreature> Codex)? __state)
    {
        if (__state is not { } own || EClass.game?.player?.recipes is not { } recipes) {
            return;
        }

        recipes.knownRecipes = own.Known;
        recipes.craftedRecipes = own.Crafted;
        EClass.player.codex.creatures = own.Codex;
    }

    // ---- lists

    /// <summary>
    ///     A copy of what that Player object knows
    /// </summary>
    internal static TeamLore Take(Player player)
    {
        return new() {
            Known = new(player.recipes.knownRecipes),
            Crafted = [..player.recipes.craftedRecipes],
            Codex = player.codex.creatures.ToDictionary(c => c.Key, c => (int[])c.Value._ints.Clone()),
        };
    }

    /// <summary>
    ///     That Player object knows this, and nothing else (copies)
    /// </summary>
    internal static void Put(Player player, TeamLore lore)
    {
        player.recipes.knownRecipes = new(lore.Known);
        player.recipes.craftedRecipes = [..lore.Crafted];
        player.codex.creatures = lore.Codex.ToDictionary(c => c.Key, c => new CodexCreature {
            id = c.Key,
            _ints = Ints(c.Value),
        });
    }

    // (five numbers, whatever was stored)
    private static int[] Ints(int[]? stored)
    {
        var ints = new int[5];
        stored?.Take(5).ToArray().CopyTo(ints, 0);
        return ints;
    }

    /// <summary>
    ///     Everything these teams know: the most times a recipe was learnt, the most of each codex number, the flags
    ///     of all
    /// </summary>
    internal static TeamLore Union(IEnumerable<TeamLore> lores)
    {
        var union = new TeamLore();
        var crafted = new HashSet<string>();
        foreach (var lore in lores) {
            foreach (var (id, times) in lore.Known) {
                union.Known[id] = System.Math.Max(union.Known.GetValueOrDefault(id), times);
            }

            crafted.UnionWith(lore.Crafted);
            foreach (var (id, ints) in lore.Codex) {
                var into = union.Codex.TryGetValue(id, out var have) ? have : union.Codex[id] = new int[5];
                var from = Ints(ints);
                for (var i = 0; i < into.Length; i++) {
                    // (the second number holds flags: the card given once)
                    into[i] = i == 1 ? into[i] | from[i] : System.Math.Max(into[i], from[i]);
                }
            }
        }

        union.Crafted = [..crafted];
        return union;
    }

    /// <summary>
    ///     What that team knows in the ledger, none when it has no entry (or one that cannot be read)
    /// </summary>
    internal static TeamLore? Stored(int team)
    {
        if (!ElinNetHost.TeamLoreBook.TryGetValue(team, out var text)) {
            return null;
        }

        try {
            return JsonConvert.DeserializeObject<TeamLore>(text);
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "The knowledge of team {TeamId} could not be read", team);
            return null;
        }
    }

    internal static void Store(int team, TeamLore lore)
    {
        ElinNetHost.TeamLoreBook[team] = JsonConvert.SerializeObject(lore);
    }

    /// <summary>
    ///     What the bench reads: each team's numbers of recipes, crafted recipes and creatures
    /// </summary>
    internal static string Describe()
    {
        var lines = ElinNetHost.TeamLoreBook.Keys.OrderBy(k => k).Select(team => Stored(team) is { } lore
            ? $"{StoryOwner.Name(team)} recipes {lore.Known.Count} crafted {lore.Crafted.Count} codex {lore.Codex.Count}"
            : $"{StoryOwner.Name(team)} unreadable");
        return $"schema {ElinNetHost.TeamLoreState[0]} seeded {ElinNetHost.TeamLoreState[1]}\n" + string.Join("\n", lines);
    }
}
