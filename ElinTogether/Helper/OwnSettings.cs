using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Net;
using Newtonsoft.Json;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     What is a player's own but lives in the game it plays (Game.config: auto combat type, tactics, zoom;
///     Player: window places, widgets, hotbars, sort, trackers) or only on its screen (the floating windows: bag,
///     open containers it carries, abilities). <br />
///     A player alone keeps all of it from map to map. A client gets a whole new game each time the host sends its
///     world (ElinNetClient.OnSaveDataProbe): the windows are closed with the old one, and the settings of the
///     new one are the host's. Noted here when a game goes away, put into the next one, the windows opened again
///     once the map is in place. Also written to a file of this machine, per world and character, never in a
///     save: a player who joins again finds its own
/// </summary>
internal static class OwnSettings
{
    private static Kept? _kept;
    private static bool _reopen;

    private static string Root => field ??= Path.Combine(Application.persistentDataPath, "ElinMP",
#if DEBUG
        // test instances on one machine share this folder
        EmpConfig.Dev.Identity.Value > 0 ? $"OwnSettings_{EmpConfig.Dev.Identity.Value}" :
#endif
        "OwnSettings");

    /// <summary>
    ///     A magic domain the game does not know (no element of that id) makes the game's own code throw where it
    ///     goes through them, the dream spell at each wake-up (Player.DreamSpell: Element.Create gives null). Seen
    ///     in the release gate of 2026-11-08 on a guest after a dozen suites, origin not found: dropped, and named
    /// </summary>
    internal static void DropUnknownDomains(Player player)
    {
        if (player.domains is not { } domains) {
            player.domains = [];
            return;
        }

        var unknown = domains.Where(id => !EClass.sources.elements.map.ContainsKey(id)).ToList();
        if (unknown.Count == 0) {
            return;
        }

        domains.RemoveAll(unknown.Contains);
        EmpLog.Warning("Magic domains unknown to the game dropped: {Domains}", string.Join(", ", unknown));
    }

    /// <summary>
    ///     Before the game of a client goes away (a world received, see ElinNetClient.OnSaveDataProbe, or the game
    ///     left, see OwnSettingsPatch)
    ///     <paramref name="now" />: written down while the game goes on (its respawn point or home changed, see
    ///     <see cref="WatchRespawn" />), so that a game that ends without a word (a crash, the window closed) still
    ///     finds it; the game going away notes it again
    /// </summary>
    internal static void Keep(bool now = false)
    {
        // a second world before the first one stood on a map: what was kept for the first is still good
        if (!EClass.core.IsGameStarted || Game.id != ResourceFetch.EmpSaveId || EClass.pc is not { } pc ||
            EClass.player is not { } player || (!now && ReferenceEquals(_kept?.From, player))) {
            return;
        }

        try {
            // as Game.Save writes them down: place and size of the floating windows, place of the widgets
            player.OnBeforeSave();
            EClass.ui.widgets.UpdateConfigs();

            var kept = new Kept {
                // (noted while the game goes on: the game going away is still to be noted)
                From = now ? _kept?.From : player,
                Seed = EClass.game.seed,
                Chara = pc.uid,
                Config = EClass.game.config,
                Pref = player.pref,
                Windows = player.dataWindow,
                AbilityConfig = player.layerAbilityConfig,
                MainWidgets = player.mainWidgets,
                SubWidgets = player.subWidgets,
                SubTheme = player.useSubWidgetTheme,
                AllyInv = player.windowAllyInv,
                ContainerCenter = player.openContainerCenter,
                Pick = player.dataPick,
                FavAbility = player.favAbility,
                PriorityActions = player.priorityActions,
                QuestTracker = player.questTracker,
                TrackedCategories = player.trackedCategories,
                TrackedCards = player.trackedCards,
                TrackedElements = player.trackedElements,
                LastRecipes = player.lastRecipes,
                FavMoongate = player.favMoongate,
                Cinema = player.cinemaConfig,
                HotbarPage = player.hotbarPage,
                LightMod = player.customLightMod,
                Memo = player.memo,
                Memo2 = player.memo2,
                Hoard = player.hoard,
                KnownCraft = player.knownCraft,
                Domains = [.. player.domains],
                Sketches = player.sketches,
                PlayedStories = player.flags.playedStories,
                AvailableStories = player.flags.availableStories,
                DoneBackers = player.doneBackers,
                KnownBGMs = player.knownBGMs,
                KnownSongs = player.knownSongs,
                HangIcons = player.hangIcons,
                SpawnZone = player.uidSpawnZone,
                OwnSpawn = true,
                HomeZone = pc._cints[2],
                LastTown = player.uidLastTown,
                Hotbars = player.hotbars,
                PartySetups = player.partySetups,
                Flags = DialogFlagSync.OwnFlags(player),
                Deepest = player.stats.deepest,
                DeepestVoid = player.stats.deepestVoid,
                LastChuryu = player.stats.lastChuryu,
                Mama = player.stats.mama,
                EscapeSilverBell = player.stats.escapeSilverBell,
                QuestRerollCost = player.questRerollCost,
                SafeTravel = player.safeTravel,
                StaminaRecovery = player.staminaRecovery,
                TutorialStep = player.tutorialStep,
                SeedFortune = player.seedFortune,
                LuckyCoin = player.luckycoin,
                LastFelmeraReward = player.lastFelmeraReward,
                GiftJure = player.giftJure,
                PrayedDay = player.prayed ? EClass.world.date.GetRawDay() : null,
                FishArtifact = player.fishArtifact,
                SeedShrine = player.seedShrine,
                LittleSaved = player.little_saved,
                LittleDead = player.little_dead,
                TotalFeat = player.totalFeat,
                DistanceTravel = player.distanceTravel,
                DateTravel = player.dateTravel,
                LastTravelZone = player.uidLastTravelZone,
                Log = EClass.game.log,
                Digs = player.stats.digs,
                Deaths = player.stats.death,
                EarlyDeaths = player.stats.death2,
                Slept = player.stats.slept,
                Ability = EClass.game.altAbility && EClass.ui.IsAbilityOpen,
                ZoomOut = ActionMode.Adv?.zoomOut2 ?? false,
            };

            foreach (var thing in pc.things.Flatten()) {
                if (thing.c_windowSaveData is { } data) {
                    kept.Bags[thing.uid] = data;
                }
            }

            foreach (var layer in LayerInventory.listInv) {
                if (!layer.IsFloat) {
                    continue;
                }

                if (layer.mainInv) {
                    kept.Inventory = true;
                } else if (layer.GetPlayerContainer() is { } bag) {
                    kept.Open.Add(bag.uid);
                }
            }

            _kept = kept;

            // the file: the hotbars too (the spells laid on the belt were lost at each new session), without the
            // cards and zones their items point at, which would be written whole: only their numbers
            var held = new List<(HotItem Item, Card? Thing, Zone? Zone)>();
            Hot(kept.Hotbars, (item, bar, page, slot) => {
                switch (item) {
                    case HotItemThing { thing: not null } hot:
                        kept.HotRefs.Add([bar, page, slot, hot.thing.uid]);
                        held.Add((hot, hot.thing, null));
                        hot.thing = null;
                        break;
                    case HotItemFocusPos { zone: not null } focus:
                        kept.HotRefs.Add([bar, page, slot, focus.zone.uid]);
                        held.Add((focus, null, focus.zone));
                        focus.zone = null;
                        break;
                }
            });

            try {
                Directory.CreateDirectory(Root);
                File.WriteAllBytes(PathOf(kept.Seed, kept.Chara), LZ4Bytes.Create(kept).Bytes);
            } finally {
                foreach (var (item, thing, zone) in held) {
                    switch (item) {
                        case HotItemThing hot:
                            hot.thing = thing as Thing;
                            break;
                        case HotItemFocusPos focus:
                            focus.zone = zone;
                            break;
                    }
                }
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not note this player's own settings before its game goes away");
        }
    }

    // the respawn point and home last seen in this game, see WatchRespawn
    private static (Player? Of, int Spawn, int Home) _watched;

    /// <summary>
    ///     Each frame on a client: the player set its respawn point or its home (the zone core, a waystone): noted at
    ///     once, see <see cref="Keep" />
    /// </summary>
    internal static void WatchRespawn()
    {
        if (!EClass.core.IsGameStarted || Game.id != ResourceFetch.EmpSaveId || EClass.player is not { } player ||
            EClass.pc is not { _cints: not null } pc) {
            return;
        }

        (Player? Of, int Spawn, int Home) seen = (player, player.uidSpawnZone, pc._cints[2]);
        if (seen == _watched) {
            return;
        }

        var changed = ReferenceEquals(_watched.Of, player);
        _watched = seen;
        if (changed) {
            EmpLog.Information("Respawn point of this player now {SpawnZone}, home {HomeZone}", seen.Spawn, seen.Home);
            Keep(true);
        }
    }

    /// <summary>
    ///     Where a player with neither a respawn point nor a home wakes up after its death: the start site, or the last
    ///     town, never nowhere (a guest's home can be unset, CharaImport.Adopt)
    /// </summary>
    internal static Zone? RespawnFallback()
    {
        return EClass.game.StartZone ?? EClass.game.spatials.Find(EClass.player.uidLastTown);
    }

    /// <summary>
    ///     The new game is in place, not started yet (before Game.OnGameInstantiated, which reads the widgets, and
    ///     Player.OnLoad, which reads dataWindow): the settings of this player go in, in place of the host's
    /// </summary>
    internal static void Carry(Chara chara)
    {
        _reopen = false;
        if (EClass.game?.player is not { } player) {
            return;
        }

        try {
            // nothing noted for this character in this world (first world of this session): the file
            var kept = _kept is { } noted && noted.Seed == EClass.game.seed && noted.Chara == chara.uid
                ? noted
                : Read(EClass.game.seed, chara.uid);
            _kept = kept;

            // (nothing noted: a first time in this world, its own counts start at zero, not at the host's)
            DialogFlagSync.SetOwnFlags(player, kept?.Flags);
            // the depth reached is this player's own (see BossWinDelta), not the host's
            player.stats.deepest = kept?.Deepest ?? 0;
            player.stats.deepestVoid = kept?.DeepestVoid ?? 0;
            // and the count of cats it brought along (hunt 5, H5-15): shown by the "nyan" key until the next
            // Player.UpdateNyan. (The "you have played N hours" count, Player.realHour, is static: per process, so
            // already the player's own)
            player.stats.lastChuryu = kept?.LastChuryu ?? 0;
            // and the counters the game keeps for its own character: the mama's gifts, the silver bell escapes, the
            // quest reroll price, the safe-travel and stamina-recovery reserves, how far the tutorial went
            player.stats.mama = kept?.Mama ?? 0;
            player.stats.escapeSilverBell = kept?.EscapeSilverBell ?? 0;
            player.questRerollCost = kept?.QuestRerollCost ?? 0;
            player.safeTravel = kept?.SafeTravel ?? 0;
            player.staminaRecovery = kept?.StaminaRecovery ?? 0;
            player.tutorialStep = kept?.TutorialStep ?? 0;
            // so are its fortune rolls and lucky coins (see FortuneCountersDelta)
            player.seedFortune = kept?.SeedFortune ?? 0;
            player.luckycoin = kept?.LuckyCoin ?? 0;
            // and its own reward history (audit H5-07): the Felmera sketch rewards already claimed (counted against
            // its own sketches, which are kept above), the Jure gifts opened, the fishing artifact luck, the shrines
            // opened, the little ones saved and lost. A world fact stays the host's: the stats.* counts
            player.lastFelmeraReward = kept?.LastFelmeraReward ?? 0;
            player.giftJure = kept?.GiftJure ?? 0;
            // and its daily prayer: the copy holds whether the host prayed today, so a host who had prayed made the
            // guest's god ignore it (no faith experience), and one who had not let the guest pray twice after a
            // rejoin. Prayed only if noted on this very day (WorldDateAdvanceDelta clears it at each new day)
            player.prayed = kept?.PrayedDay is { } prayedDay && EClass.game.world?.date?.GetRawDay() == prayedDay;
            player.fishArtifact = kept?.FishArtifact ?? 0;
            player.seedShrine = kept?.SeedShrine ?? 0;
            player.little_saved = kept?.LittleSaved ?? 0;
            player.little_dead = kept?.LittleDead ?? 0;
            // and the levels it gained (the character sheet's "total feat", hunt 6 item 7): the host's count. None
            // noted (a first time, or an older file): one per level above the first, as Card.LevelUp counts them
            player.totalFeat = kept?.TotalFeat ?? Math.Max(0, chara.LV - 1);
            // and its journey (hunt 6 item 3): the steps it walked on its own world map, since when, from which town.
            // Player.MoveZone pays them in experience on arrival in another town, which for a guest walking into
            // the host's map is in the new game (EnterHostMap): with the host's journey there, the guest's was lost.
            // None noted: no journey yet. (Its own clock may have run ahead of this world's while away: not later than now)
            player.distanceTravel = kept?.DistanceTravel ?? 0;
            var now = EClass.game.world?.date?.GetRaw() ?? player.dateTravel;
            player.dateTravel = Math.Min(kept?.DateTravel ?? now, now);
            player.uidLastTravelZone = kept?.LastTravelZone ?? 0;
            // the journal tells about this player (council 2026-10-28, OwnJournal): its own message history, not the
            // host's last lines (a first time in this world: none yet)
            if (NetSession.Instance.Rules.OwnJournal) {
                EClass.game.log = kept?.Log ?? new MsgLog { id = "log" };
                // and its own records, counted by the game only for its own character: blocks dug (hall of fame),
                // deaths (a dialog reads them), nights slept (also the seed of the recipe it dreams of). Kills stay
                // the world's: the game counts the whole faction's, and a story quest reads them (QuestDefense)
                player.stats.digs = kept?.Digs ?? 0;
                player.stats.death = kept?.Deaths ?? 0;
                player.stats.death2 = kept?.EarlyDeaths ?? 0;
                player.stats.slept = kept?.Slept ?? 0;
            }

            // the party setups this player saved (the party window's five slots, hunt 6 item 6): the host's slots
            // listed the host's companions (its own keep their numbers in the host's copy). None noted: empty
            player.partySetups = kept?.PartySetups ?? new Player.PartySetup[Player.MaxPartySetup];

            // where this player wakes up after its death (TraitCoreZone, TraitWaystone: Player.spawnZone, then its
            // home, see Scene): its own, never the one of the copy, which is the host's (its meadow). Left as it was,
            // a guest who set its home on its own land, or had never set a respawn point, woke up on the host's
            // meadow, and the next note wrote the host's meadow down as the guest's own. A file of an older version
            // may hold that copy: the host's value there is dropped (its home, then the start site decide)
            var copySpawn = player.uidSpawnZone;
            player.uidSpawnZone = kept is { SpawnZone: not 0 and var spawn } && (kept.OwnSpawn || spawn != copySpawn) ? spawn : 0;
            // and the home it set (TraitCoreZone "set as home"), on its character, which the host's copy may not hold
            if (kept is { HomeZone: not 0 and var home } && EClass.game.spatials.map.ContainsKey(home)) {
                chara._cints[2] = home;
            }

            _watched = (player, player.uidSpawnZone, chara._cints[2]);

            // its biography: the one it brought from its own save, the first time in this world (see BackgroundFile)
            if (ImportedBackground is { } imported && BackgroundPath(EClass.game.seed, chara.uid) is var bio && !File.Exists(bio)) {
                try {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(bio, imported);
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Could not keep the biography this player brought at {Path}", bio);
                }
            }

            ImportedBackground = null;

            if (kept is null) {
                return;
            }

            EClass.game.config = kept.Config ?? EClass.game.config;
            // built from the auto combat type of the game it was first asked in
            chara._tactics = null;

            player.pref = kept.Pref ?? player.pref;
            player.dataWindow = kept.Windows ?? player.dataWindow;
            player.layerAbilityConfig = kept.AbilityConfig ?? player.layerAbilityConfig;
            player.mainWidgets = kept.MainWidgets ?? player.mainWidgets;
            player.subWidgets = kept.SubWidgets ?? player.subWidgets;
            player.useSubWidgetTheme = kept.SubTheme;
            player.windowAllyInv = kept.AllyInv ?? player.windowAllyInv;
            player.openContainerCenter = kept.ContainerCenter;
            player.dataPick = kept.Pick ?? player.dataPick;
            player.favAbility = kept.FavAbility ?? player.favAbility;
            player.priorityActions = kept.PriorityActions ?? player.priorityActions;
            player.questTracker = kept.QuestTracker;
            player.trackedCategories = kept.TrackedCategories ?? player.trackedCategories;
            player.trackedCards = kept.TrackedCards ?? player.trackedCards;
            player.trackedElements = kept.TrackedElements ?? player.trackedElements;
            player.lastRecipes = kept.LastRecipes ?? player.lastRecipes;
            player.favMoongate = kept.FavMoongate ?? player.favMoongate;
            player.cinemaConfig = kept.Cinema ?? player.cinemaConfig;
            player.hotbarPage = kept.HotbarPage;
            player.customLightMod = kept.LightMod;
            player.memo = kept.Memo ?? "";
            player.memo2 = kept.Memo2 ?? "";
            // (its respawn point: above, before an empty note returns)
            player.uidLastTown = kept.LastTown;

            // what this player collected or learned (the hoard, crafts and sketches seen, tunes heard and played), not
            // the world: the host's copy of them is the host's own. Nothing noted (an older file): the copy stays
            player.hoard = kept.Hoard ?? player.hoard;
            player.knownCraft = kept.KnownCraft ?? player.knownCraft;
            player.sketches = kept.Sketches ?? player.sketches;
            // the magic domains it picked: RefreshDomain (ElinNetClientPlayer, before this) reset them to the job's
            player.domains = kept.Domains is { } domains ? [.. domains] : player.domains;
            DropUnknownDomains(player);
            // the story scenes this player saw or can see (Nymelle, Lothria, the base): the host's list skipped a
            // scene the guest never saw or played one again (hunt 5, H5-20)
            player.flags.playedStories = kept.PlayedStories ?? player.flags.playedStories;
            player.flags.availableStories = kept.AvailableStories ?? player.flags.availableStories;
            // the supporter characters and trees this player already dealt with (hunt 5, H5-08): the host's list made
            // them come back for the guest, or hid ones it never met
            player.doneBackers = kept.DoneBackers ?? player.doneBackers;
            player.knownBGMs = kept.KnownBGMs ?? player.knownBGMs;
            player.knownSongs = kept.KnownSongs ?? player.knownSongs;
            player.hangIcons = kept.HangIcons ?? player.hangIcons;

            // the cards are other objects in this copy of the world: found again by number
            var things = chara.things.Flatten().ToList();
            foreach (var thing in things) {
                if (kept.Bags.TryGetValue(thing.uid, out var data)) {
                    // closing the window with the old game marked it closed
                    data.open |= kept.Open.Contains(thing.uid);
                    thing.c_windowSaveData = data;
                }
            }

            _reopen = true;

            if (kept.Hotbars is not { } hotbars) {
                return;
            }

            // hotbars hold cards and zones, other objects in this copy of the world: found again by number
            // (read from the file they are not there at all, only their numbers are)
            player.hotbars = hotbars;
            foreach (var bar in hotbars.bars) {
                // its widget went with the old game
                bar?.actor = null;
            }

            Hot(hotbars, (item, bar, page, slot) => {
                var noted = kept.HotRefs.Find(r => r[0] == bar && r[1] == page && r[2] == slot)?[3] ?? 0;
                switch (item) {
                    case HotItemThing hot:
                        hot.thing = things.Find(t => t.uid == (hot.thing?.uid ?? noted));
                        if (hot.thing is null) {
                            hotbars.bars[bar].pages[page].items[slot] = null;
                        }

                        break;
                    case HotItemFocusPos focus when (focus.zone?.uid ?? noted) is var zone and not 0:
                        focus.zone = EClass.game.spatials.Find(zone);
                        break;
                }
            });
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not carry this player's own settings over to the world received");
        }
    }

    /// <summary>
    ///     The map is in place (Scene.Init is done): the windows that were open are opened again, the way Game.Load
    ///     does after a save is loaded. Never twice, never for a dead player, only what the player carries
    /// </summary>
    internal static void Reopen()
    {
        if (!_reopen || _kept is not { } kept) {
            return;
        }

        _reopen = false;

        try {
            if (EClass.pc is not { isDead: false } pc) {
                return;
            }

            SoundManager.ignoreSounds = true;

            if (EClass.game.altInv) {
                if (kept.Inventory && !LayerInventory.listInv.Exists(l => l.mainInv)) {
                    // the bag, and the containers in it whose window is marked open
                    EClass.ui.OpenFloatInv(true);
                    SoundManager.ignoreSounds = true;
                }

                // a container opened on its own, or one inside another
                foreach (var thing in pc.things.Flatten().ToList()) {
                    if (kept.Open.Contains(thing.uid) && thing.trait is { IsContainer: true } and not TraitToolBelt &&
                        !LayerInventory.IsOpen(thing)) {
                        LayerInventory.CreateContainerPC(thing);
                    }
                }
            }

            if (kept.Ability && EClass.game.altAbility && !EClass.ui.IsAbilityOpen) {
                EClass.ui.ToggleAbility();
            }

            if (ActionMode.Adv is { } adv) {
                adv.zoomOut2 = kept.ZoomOut;
            }

            TooltipManager.Instance.HideTooltips(immediate: true);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not open the windows again after the world was replaced");
        } finally {
            SoundManager.ignoreSounds = false;
        }
    }

    /// <summary>
    ///     Every item of the hotbars, with its bar, page and slot
    /// </summary>
    private static void Hot(HotbarManager? hotbars, Action<HotItem, int, int, int> each)
    {
        for (var b = 0; b < (hotbars?.bars?.Length ?? 0); b++) {
            if (hotbars!.bars[b] is not { pages: not null } bar) {
                continue;
            }

            for (var p = 0; p < bar.pages.Count; p++) {
                for (var i = 0; i < (bar.pages[p]?.items?.Count ?? 0); i++) {
                    if (bar.pages[p].items[i] is { } item) {
                        each(item, b, p, i);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     The biography of a character brought from a save of this player (CharaImport), until its first world
    /// </summary>
    internal static string? ImportedBackground { get; set; }

    internal static string? ReadBackground(string saveFolder)
    {
        try {
            var path = Path.Combine(saveFolder, "background.txt");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not read the biography of save {Path}", saveFolder);
            return null;
        }
    }

    /// <summary>
    ///     The file of the biography of "the player" (Player.GetBackgroundText, EditBackgroundText: background.txt of
    ///     the save). A client's save is a copy of the host's world, its file the host's biography: a client's is its
    ///     own, a file of this machine per world and character next to its settings. None yet: a new one, as the game
    ///     makes for a new character (Player.GenerateBackgroundText)
    /// </summary>
    internal static string BackgroundFile()
    {
        var save = GameIO.pathCurrentSave + "background.txt";
        if (Game.id != ResourceFetch.EmpSaveId || EClass.game is not { } game || EClass.pc is not { } pc) {
            return save;
        }

        var path = BackgroundPath(game.seed, pc.uid);
        try {
            if (!File.Exists(path)) {
                Directory.CreateDirectory(Root);
                var templates = new DirectoryInfo(CorePath.CorePackage.Background).GetFiles("*.txt");
                File.WriteAllText(path, templates.Length > 0 ? File.ReadAllText(templates.RandomItem().FullName) : "");
            }

            return path;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not make this player's own biography at {Path}", path);
            return save;
        }
    }

    private static string BackgroundPath(int seed, int chara)
    {
        return Path.Combine(Root, $"{seed}_{chara}_background.txt");
    }

    private static string PathOf(int seed, int chara)
    {
        return Path.Combine(Root, $"{seed}_{chara}.lz4");
    }

    private static Kept? Read(int seed, int chara)
    {
        var path = PathOf(seed, chara);
        if (!File.Exists(path)) {
            return null;
        }

        try {
            var kept = new LZ4Bytes { Bytes = File.ReadAllBytes(path) }.Decompress<Kept>();
            return kept is { } read && read.Seed == seed && read.Chara == chara ? read : null;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not read this player's own settings from {Path}", path);
            return null;
        }
    }

    /// <summary>
    ///     Written with the serializer of the saves: the game's own classes, as the game writes them
    /// </summary>
    private sealed class Kept
    {
        // which world (the host's copy carries its seed), which character
        public int Seed;
        public int Chara;

        public Game.Config? Config;
        public Player.Pref? Pref;
        public Dictionary<string, Window.SaveData>? Windows;
        public LayerAbility.Config? AbilityConfig;
        public WidgetManager.SaveData? MainWidgets;
        public WidgetManager.SaveData? SubWidgets;
        public bool SubTheme;
        public Window.SaveData? AllyInv;
        public bool ContainerCenter;
        public Window.SaveData? Pick;
        public HashSet<int>? FavAbility;
        public Dictionary<string, List<string>>? PriorityActions;
        public bool QuestTracker;
        public HashSet<string>? TrackedCategories;
        public HashSet<string>? TrackedCards;
        public HashSet<int>? TrackedElements;
        public Dictionary<string, string>? LastRecipes;
        public List<string>? FavMoongate;
        public CinemaConfig? Cinema;
        public int HotbarPage;
        public int LightMod;
        public string? Memo;
        public string? Memo2;
        public Hoard? Hoard;
        public HashSet<int>? KnownCraft;
        public HashSet<int>? Sketches;
        public List<int>? Domains;
        public List<int>? PlayedStories;
        public List<int>? AvailableStories;
        public HashSet<int>? DoneBackers;
        public HashSet<int>? KnownBGMs;
        public Dictionary<string, KnownSong>? KnownSongs;
        public Dictionary<string, string>? HangIcons;
        public int SpawnZone;

        // SpawnZone is the player's own (written since 2026-10-30; an older file may hold the host's meadow)
        public bool OwnSpawn;

        // the home of its character (Chara.homeZone)
        public int HomeZone;
        public int LastTown;
        public Player.PartySetup[]? PartySetups;

        // windows of the cards the player carries, by card number; which of them were open
        public Dictionary<int, Window.SaveData> Bags = [];
        public HashSet<int> Open = [];
        public bool Inventory;
        public bool Ability;
        public bool ZoomOut;

        // the cards and zones the items of the hotbars point at, by number: bar, page, slot, number
        public HotbarManager? Hotbars;
        public List<int[]> HotRefs = [];

        // the counts the game keeps for "the player" that are this player's own, see DialogFlagSync
        public Dictionary<string, int>? Flags;
        public int Deepest;
        public int DeepestVoid;
        public int LastChuryu;
        public long Mama;
        public int EscapeSilverBell;
        public int QuestRerollCost;
        public int SafeTravel;
        public int StaminaRecovery;
        public int TutorialStep;
        public int SeedFortune;
        public int LuckyCoin;
        public int LastFelmeraReward;
        public int GiftJure;

        // the day this player last prayed (Player.prayed, Date.GetRawDay), none if not today when noted
        public int? PrayedDay;
        public int FishArtifact;
        public int SeedShrine;
        public int LittleSaved;
        public int LittleDead;
        public int? TotalFeat;
        public int DistanceTravel;
        public int DateTravel;
        public int LastTravelZone;

        // the message history (the journal's log), see OwnJournal
        public MsgLog? Log;
        public int Digs;
        public int Deaths;
        public int EarlyDeaths;
        public int Slept;

        // the game these were noted from: not noted twice
        [JsonIgnore]
        public Player? From;
    }
}
