using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     What the story remembers is the same for every player, like the quest log: dialog flags, story flags,
///     key items, the debt, guild memberships. These are written all over the game, so they are compared with what was last shared
///     instead of being caught where they change
/// </summary>
internal static class DialogFlagSync
{
    private const float Interval = 0.5f;

    private const string Dialog = "d:";
    private const string Story = "f:";
    private const string KeyItem = "k:";
    private const string Debt = "p:debt";
    // the base's monthly bill is the host's (WorldKeeperHooks): a guest's choice of an extra tax (the maid, an
    // investor) was kept in its own game and never paid (hunt 3 row 50)
    private const string ExtraTax = "p:extraTax";
    private const string Guild = "g:";
    // a town's influence (quests, bloom, rerolls): each game kept its own (hunt 4, 2026-10-25)
    private const string Influence = "z:";

    /// <summary>
    ///     Flags about one player's screen or body, not about the story
    /// </summary>
    private static readonly HashSet<string> _personal = [
        nameof(Player.Flags.gotClickReward),
        nameof(Player.Flags.welcome),
        nameof(Player.Flags.helpHighlightDisabled),
        nameof(Player.Flags.isShoesOff),
        nameof(Player.Flags.backpackHighlightDisabled),
        nameof(Player.Flags.abilityHighlightDisabled),
        nameof(Player.Flags.toggleHotbarHighlightDisabled),
        nameof(Player.Flags.toggleHotbarHighlightActivated),
        nameof(Player.Flags.debugEnabled),
        nameof(Player.Flags.gotMelilithCurse),
        nameof(Player.Flags.gotEtherDisease),
        // counted by each game on its own clock: the host's count is the one that matters
        nameof(Player.Flags.daysAfterQuestExploration),
        nameof(Player.Flags.magicChestSent),
        // counted for each player: see _own
        nameof(Player.Flags.landDeedBought),
        nameof(Player.Flags.garokkHammerBought),
        nameof(Player.Flags.reward_killkill),
        nameof(Player.Flags.reward_gould),
        nameof(Player.Flags.canComupWithFoodRecipe),
        nameof(Player.Flags.pickedMelilithTreasure),
    ];

    /// <summary>
    ///     Each player's own count, where the game counts "the player": the price of a land deed and of Garokk's
    ///     hammer doubles with each one bought, the prizes of a musician get rarer with each one won, a cook may come
    ///     up with a recipe once. Common, the second player paid double and won half as often for what the first
    ///     did. A guest's game gets the host's Player with every copy of the world: its own values are kept aside
    ///     and put back (OwnSettings), and start at zero the first time
    /// </summary>
    // ponytail: kept in the player's own settings file, on its machine: another machine starts at zero again, and
    // a guest that takes the world over starts from the former host's counts. Keep them on the character if it matters
    private static readonly PropertyInfo[] _own = new[] {
        nameof(Player.Flags.landDeedBought),
        nameof(Player.Flags.garokkHammerBought),
        nameof(Player.Flags.reward_killkill),
        nameof(Player.Flags.reward_gould),
        nameof(Player.Flags.canComupWithFoodRecipe),
        nameof(Player.Flags.gotMelilithCurse),
        nameof(Player.Flags.gotEtherDisease),
        // (the first treasure of the cursed manor is allowed to each player, see MelilithTreasurePatch)
        nameof(Player.Flags.pickedMelilithTreasure),
    }.Select(name => typeof(Player.Flags).GetProperty(name)!).ToArray();

    internal static Dictionary<string, int> OwnFlags(Player player)
    {
        return _own.ToDictionary(flag => flag.Name, flag => flag.GetValue(player.flags) switch {
            bool set => set ? 1 : 0,
            int number => number,
            _ => 0,
        });
    }

    internal static void SetOwnFlags(Player player, Dictionary<string, int>? values)
    {
        foreach (var flag in _own) {
            var value = values?.GetValueOrDefault(flag.Name) ?? 0;
            flag.SetValue(player.flags, flag.PropertyType == typeof(bool) ? value != 0 : value);
        }
    }

    private static readonly PropertyInfo[] _storyFlags = typeof(Player.Flags)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int)))
        .Where(p => !_personal.Contains(p.Name))
        .ToArray();

    private static readonly Dictionary<string, int> _shared = [];
    private static Player? _source;
    private static float _next;

    internal static void Tick()
    {
        if (Time.unscaledTime < _next) {
            return;
        }

        _next = Time.unscaledTime + Interval;

        TellChanges();
        PersonalQuests.Tick();
        SharedQuests.TellChanges();
        OrphanEscorts.Tick();
    }

    /// <summary>
    ///     Right now, not at the next half second: before a message that builds on it
    /// </summary>
    internal static void TellChanges()
    {
        if (!EClass.core.IsGameStarted || EClass.player is not { } player) {
            return;
        }

        MelilithTreasurePatch.MarkOldWorld(player);

        var current = Read(player);

        if (!ReferenceEquals(player, _source)) {
            // another world, or another copy of it: what it holds is what it was given
            _source = player;
            _shared.Clear();
            foreach (var (id, value) in current) {
                _shared[id] = value;
            }

            return;
        }

        foreach (var (id, value) in current) {
            if (_shared.TryGetValue(id, out var shared) && shared == value) {
                continue;
            }

            _shared[id] = value;

            var delta = new DialogFlagDelta {
                Id = id,
                Value = value,
            };
            QuestAwaySync.Send(delta);
            NetSession.Instance.Connection?.Delta.AddRemote(delta);
        }
    }

    /// <summary>
    ///     What this game holds under that id now, null when it holds nothing there
    /// </summary>
    internal static int? ValueOf(string id)
    {
        return EClass.player is { } player && Read(player).TryGetValue(id, out var value) ? value : null;
    }

    /// <summary>
    ///     What this game holds now under the ids that pass <paramref name="which" /> is what it was given: nothing to
    ///     tell (a story put in whole, see TeamOwnStory)
    /// </summary>
    internal static void Relearn(System.Func<string, bool> which)
    {
        if (EClass.player is not { } player || !ReferenceEquals(player, _source)) {
            return;
        }

        foreach (var (id, value) in Read(player)) {
            if (which(id)) {
                _shared[id] = value;
            }
        }
    }

    internal static void Learn(string id, int value)
    {
        if (EClass.player is not { } player) {
            return;
        }

        if (id.StartsWith(Dialog)) {
            player.dialogFlags[id[Dialog.Length..]] = value;
        } else if (id.StartsWith(KeyItem) && int.TryParse(id[KeyItem.Length..], out var item)) {
            player.keyItems[item] = value;
        } else if (id == Debt) {
            player.debt = value;
        } else if (id == ExtraTax) {
            player.extraTax = value;
        } else if (id.StartsWith(Influence)) {
            if (!int.TryParse(id[Influence.Length..], out var uid) || EClass.game.spatials.Find(uid) is not Zone zone) {
                return;
            }

            zone.influence = value;
        } else if (id.StartsWith(Guild)) {
            // g:<guild>:<t|r|e>
            var parts = id.Split(':');
            if (parts.Length != 3 || Guilds().FirstOrDefault(g => g.id == parts[1])?.relation is not { } relation) {
                return;
            }

            switch (parts[2]) {
                case "t":
                    relation.type = (FactionRelation.RelationType)value;
                    break;
                case "r":
                    relation.rank = value;
                    break;
                case "e":
                    relation.exp = value;
                    break;
            }
        } else if (id.StartsWith(Story) && _storyFlags.FirstOrDefault(p => p.Name == id[Story.Length..]) is { } flag) {
            flag.SetValue(player.flags, flag.PropertyType == typeof(bool) ? value != 0 : value);
        } else {
            return;
        }

        // (another owner's story in the quest log for a moment: what was told is the local story's, kept)
        if (ReferenceEquals(player, _source) && !(TeamOwnStory.Swapped && TeamOwnStory.IsStoryFlag(id))) {
            _shared[id] = value;
        }
    }

    private static IEnumerable<Faction> Guilds()
    {
        if (EClass.game?.factions is not { } factions) {
            yield break;
        }

        foreach (var guild in new Faction?[] { factions.Fighter, factions.Mage, factions.Thief, factions.Merchant }) {
            if (guild is not null) {
                yield return guild;
            }
        }
    }

    private static Dictionary<string, int> Read(Player player)
    {
        var values = new Dictionary<string, int>();

        foreach (var (id, value) in player.dialogFlags) {
            values[Dialog + id] = value;
        }

        foreach (var (item, count) in player.keyItems) {
            values[KeyItem + item] = count;
        }

        foreach (var flag in _storyFlags) {
            values[Story + flag.Name] = flag.GetValue(player.flags) switch {
                bool set => set ? 1 : 0,
                int number => number,
                _ => 0,
            };
        }

        values[Debt] = player.debt;
        values[ExtraTax] = player.extraTax;
        foreach (var spatial in EClass.game.spatials.map.Values) {
            // (back to 0 is still told, once it was)
            if (spatial is Zone zone && (zone.influence != 0 || _shared.ContainsKey(Influence + zone.uid))) {
                values[Influence + zone.uid] = zone.influence;
            }
        }

        // one membership for the group: who joined a guild, its rank and what it contributed
        foreach (var guild in Guilds()) {
            if (guild.relation is not { } relation) {
                continue;
            }

            values[$"{Guild}{guild.id}:t"] = (int)relation.type;
            values[$"{Guild}{guild.id}:r"] = relation.rank;
            values[$"{Guild}{guild.id}:e"] = relation.exp;
        }

        return values;
    }
}
