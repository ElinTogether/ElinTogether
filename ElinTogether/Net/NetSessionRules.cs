using MessagePack;

namespace ElinTogether.Net;

// Keys: the highest in use is 160 (141 unused), the next free one is 161 (145 reserved for the server avatar, 146 and 147 spare). Keep them in order and do not skip any: a MessagePack
// array writes a nil for every missing index, so a gap costs a byte per join for ever (51, 94 and 110 are the old ones)
[MessagePackObject]
public class NetSessionRules
{
    [Key(0)]
    public required bool UseSharedSpeed { get; set; }

    [Key(1)]
    public required bool UseTurnBasedCombat { get; set; }

    [Key(2)]
    public bool AllowIndependentTravel { get; set; }

    /// <summary>
    ///     Seconds between two checkpoints of an away client, 0 disables them
    /// </summary>
    [Key(3)]
    public int TravelCheckpointSeconds { get; set; }

    /// <summary>
    ///     Every player is paid for its own goods in the shipping box, otherwise the host gets everything
    /// </summary>
    [Key(4)]
    public bool UsePlayerShipping { get; set; }

    /// <summary>
    ///     What fights a player only acts on that player's turns, see PlayerCombatTime
    /// </summary>
    [Key(5)]
    public bool UsePlayerCombatTime { get; set; }

    /// <summary>
    ///     Random quests (from residents and boards) and what they earn, fame and karma, belong to the player who
    ///     takes them; story quests stay everyone's. Off: one quest log and one fame for the whole group
    /// </summary>
    [Key(6)]
    public bool UsePersonalQuests { get; set; }

    /// <summary>
    ///     Two players next to each other can trade items and gold through a window both confirm
    /// </summary>
    [Key(7)]
    public bool AllowPlayerTrade { get; set; }

    /// <summary>
    ///     The turns of a player's own character follow the clock of its own game, not the host's game time as
    ///     the network brings it. Only means something with <see cref="UsePlayerCombatTime" />: without it the
    ///     world runs on the host's clock and a guest on its own would act out of step with it
    /// </summary>
    [Key(8)]
    public bool UsePlayerClock { get; set; }

    [IgnoreMember]
    internal bool OwnClock => UsePlayerClock && UsePlayerCombatTime;

    /// <summary>
    ///     A player's own turn always lasts the base act time, as in a solo game, instead of being stretched or
    ///     shortened by the speed of the other players. Only with <see cref="UsePlayerCombatTime" />, which keeps
    ///     speed meaningful in a fight: what fights a player gets time for each of its turns
    /// </summary>
    [Key(9)]
    public bool UsePlayerStepPace { get; set; }

    [IgnoreMember]
    internal bool OwnPace => UsePlayerStepPace && UsePlayerCombatTime;

    /// <summary>
    ///     One date for the world, whoever makes time pass: a player alone on a map it simulates no longer has
    ///     a date of its own that the host's replaces when it comes back. The most advanced date is the world's
    /// </summary>
    [Key(10)]
    public bool UseSharedWorldTime { get; set; }

    /// <summary>
    ///     What a passing hour, day or month does to the world is done once, by the world keeper's game (see
    ///     Patches/WorldKeeper.cs), not by every game that makes the date advance
    /// </summary>
    [Key(11)]
    public bool UseWorldKeeper { get; set; }

    /// <summary>
    ///     The players who do not keep the map may use the build mode of the base: their clicks are asked of the
    ///     game that keeps it (AgentTaskDelta). Off: refused with a message, only that game builds
    /// </summary>
    [Key(12)]
    public bool AllowGuestBuild { get; set; } = true;

    /// <summary>
    ///     A player's strike can kill another player's character (see RemotePlayerKillPatch). Off: it is left at 0
    ///     hit points
    /// </summary>
    [Key(13)]
    public bool AllowPlayerKill { get; set; }

    /// <summary>
    ///     Only the game that keeps the base manages it: what the other players ask of it (research, hearth skills,
    ///     policies, names, settings of an object, residents, build mode) is refused with a message, and the host
    ///     sends them back the state it keeps. Off: every player manages the base as the host does
    /// </summary>
    [Key(14)]
    public bool HostManagesBase { get; set; }

    [IgnoreMember]
    internal bool GuestsBuild => AllowGuestBuild && !HostManagesBase;

    /// <summary>
    ///     The players who do not keep that map may build there: the host's rule, or the commons (every player builds)
    /// </summary>
    internal bool GuestsBuildIn(Zone? zone) => GuestsBuild || Helper.Commons.Is(zone);

    /// <summary>
    ///     Only the host manages the base of that map: never the commons' building
    /// </summary>
    internal bool HostManagesBaseIn(Zone? zone) => HostManagesBase && !Helper.Commons.Is(zone);

    /// <summary>
    ///     A player can challenge another to a duel nobody dies of (see PlayerDuel). Off: no such entry in the menu
    /// </summary>
    [Key(15)]
    public bool AllowDuels { get; set; } = true;

    /// <summary>
    ///     A player whose link with the host dropped by itself joins the same game again by itself (see
    ///     NetReconnect). Off: it is left on the title screen
    /// </summary>
    [Key(16)]
    public bool AllowReconnect { get; set; } = true;

    /// <summary>
    ///     Council 10: a player who goes to bed sleeps at once for itself; the night passes for the world when all
    ///     the players sleep at the same time. Off: everyone waits for everyone
    /// </summary>
    [Key(17)]
    public bool UseOwnSleep { get; set; } = true;

    /// <summary>
    ///     Before the council of 2026-10-23 on world map time: true for Travel Together. Still sent for a peer of an
    ///     older version, read from <see cref="WorldMapTime" /> by this one
    /// </summary>
    [Key(18)]
    public bool TimeJumpsTogether { get; set; }

    /// <summary>
    ///     Council 2026-10-23: how the travel of one player on the world map moves the date of the world, see
    ///     RemoteTravelRegionPatch
    /// </summary>
    [Key(94)]
    public WorldMapTime WorldMapTime { get; set; } = WorldMapTime.SharedClock;

    /// <summary>
    ///     Council 10: auto-dump leaves the held item and the tool belt, as it leaves the hotbar
    /// </summary>
    [Key(19)]
    public bool DumpSparesBelt { get; set; } = true;

    /// <summary>
    ///     A game whose copy of the map differs from the one of the game that keeps it loads the map again by
    ///     itself (see NetDesync). Off: the difference is only written to the journals
    /// </summary>
    [Key(20)]
    public bool AutoResync { get; set; } = true;

    /// <summary>
    ///     After each save the host makes by itself, every guest keeps a whole copy of the world on its own disk
    ///     (see Net/Handover). Off: the world is on the host's disk only
    /// </summary>
    [Key(21)]
    public bool KeepWorldCopy { get; set; } = false;

    /// <summary>
    ///     Council 10: the tax is computed on the highest fame among the connected players (see SharedTaxPatch).
    ///     Off: on the host's fame
    /// </summary>
    [Key(22)]
    public bool UseSharedTax { get; set; } = true;

    /// <summary>
    ///     Council 11: when the host walks onto the map a player holds alone, that player keeps its game and its
    ///     scene and becomes a client of the host again in place (see ZoneSoftRejoin); when that player walks into
    ///     the map the host stands on, it keeps its game and loads that one map. Off: it hands the map back and
    ///     loads the whole world again
    /// </summary>
    [Key(23)]
    public bool SoftRecall { get; set; } = false;

    /// <summary>
    ///     Council 9, step 4: when the host leaves, a guest opens the world from the copy it keeps of it and the
    ///     game goes on there (see WorldTakeover). Needs KeepWorldCopy. Off: the game ends with its host
    /// </summary>
    [Key(24)]
    public bool AllowTakeover { get; set; } = false;

    /// <summary>
    ///     Council 14: what the game gives once per world, each player gets once: the limited stock of a shop
    ///     (LimitedStockPatch) and what the story gives (ElinNetHost.NoteStoryReward). Off: the game's own rule,
    ///     the first player is the only one
    /// </summary>
    [Key(25)]
    public bool OncePerPlayer { get; set; } = true;

    /// <summary>
    ///     Council 2026-10-23: a town board is drawn for the players on the map, not the host alone (TownBoardPatch)
    /// </summary>
    [Key(51)]
    public bool TownBoards { get; set; } = true;

    /// <summary>
    ///     Audit 2026-10-23: raids, the lucky month and the days with a player count every player on the base map,
    ///     not only the one whose game runs it, see Patches/BasePlayersPatch.cs. Off: the game's own "the player"
    /// </summary>
    [Key(26)]
    public bool PlayersAtBase { get; set; } = true;

    /// <summary>
    ///     Player chat reaches every player, whatever map or zone it is on (an away player included); NPC and game
    ///     lines stay on their map. Off: a chat line reaches only the players on the same map as its speaker
    /// </summary>
    [Key(95)]
    public bool GlobalChat { get; set; } = true;

    /// <summary>
    ///     The menu on another player's character offers "Follow": the local character walks after it, see PlayerFollow
    /// </summary>
    [Key(96)]
    public bool AllowFollow { get; set; } = true;

    /// <summary>
    ///     "Talk" on another player's character opens a private chat with that player, see PlayerWhisper
    /// </summary>
    [Key(97)]
    public bool PrivateChat { get; set; } = true;

    /// <summary>
    ///     A guest's moongate: the host makes the zone of the map in its world, the guest goes alone, see GateItems.
    ///     Off: a guest's use does nothing
    /// </summary>
    [Key(98)]
    public bool GuestMoongate { get; set; } = true;

    /// <summary>
    ///     A guest's landing portal takes it to the host's survival gate zone, the host stays. Off: nothing happens
    /// </summary>
    [Key(99)]
    public bool GuestLandingPortal { get; set; } = true;

    /// <summary>
    ///     A guest's sky helm: the host's list on its screen, its tickets pay, the host sets the gate zone for
    ///     everyone. Off: nothing happens
    /// </summary>
    [Key(100)]
    public bool GuestSkyHelm { get; set; } = true;

    /// <summary>
    ///     Council 2026-10-24: a resident is married to the character who married it, the host keeps who for every
    ///     player and runs marriage, wedding, divorce and the lost ring for whoever acts, see SharedMarriages
    /// </summary>
    [Key(101)]
    public bool SharedMarriages { get; set; } = true;

    /// <summary>
    ///     Council 2026-10-25: players can be in teams, the host keeps them in its save, see Teams
    /// </summary>
    [Key(102)]
    public bool UseTeams { get; set; }

    /// <summary>
    ///     A base is its claimer's team's: the other teams cannot open its chests, pick up, build or manage it there
    /// </summary>
    [Key(103)]
    public bool TeamBases { get; set; }

    /// <summary>
    ///     Inside a team, a container, door or bed a player installs is that player's until shared
    /// </summary>
    [Key(110)]
    public bool TeamPersonal { get; set; }

    /// <summary>
    ///     Two players of different teams are enemies outside towns and the spawn zone, see TeamPvp
    /// </summary>
    [Key(111)]
    public bool TeamPvp { get; set; }

    // the six fallbacks a guest was never told (council 2026-10-25 settings trim): its card names them when the host
    // turns one off; an older host sends none of them and they read as on, their default
    [Key(112)]
    public bool TownLife { get; set; } = true;

    [Key(113)]
    public bool ShopsFromEveryPlayer { get; set; } = true;

    [Key(114)]
    public bool NearestSpawns { get; set; } = true;

    [Key(115)]
    public bool ActivitiesFromActor { get; set; } = true;

    [Key(116)]
    public bool AutoCombatPerPlayer { get; set; } = true;

    [Key(117)]
    public bool SummonsFollowMaster { get; set; } = true;

    /// <summary>
    ///     Council 2026-10-26 (MMO world time): a player without input for that many minutes is away from the
    ///     keyboard, see PlayerIdle. 0: off, also what an older host reads as (it never asked)
    /// </summary>
    [Key(118)]
    public int IdleMinutes { get; set; }

    /// <summary>
    ///     Council 2026-10-26: how many of the online, active players must sleep for the night of the world, with
    ///     <see cref="UseOwnSleep" />. An older host sends none: everyone, as it did
    /// </summary>
    [Key(119)]
    public NightRule NightPasses { get; set; } = NightRule.Everyone;

    /// <summary>
    ///     Council 2026-10-26 (MMO world time, stage 3): while no player is connected and active, the date of the
    ///     world stands still, see EmptyWorld. An older host sends none: time goes on, as it did
    /// </summary>
    [Key(120)]
    public bool TimeStopsWhenEmpty { get; set; }

    /// <summary>
    ///     Permadeath worlds only: a dead player's character waits for a revive at a bartender instead of the game
    ///     ending (council 2026-10-27, GitHub issue 9)
    /// </summary>
    [Key(121)]
    public bool PermadeathRevive { get; set; }

    /// <summary>
    ///     Council 2026-10-26 (MMO world time, stage 5): the days a long catch-up replays at most, the time before
    ///     them is skipped over (WorldDateAdvanceEvent). Every game that holds a map uses the host's. An older host
    ///     sends none: a week, as it did
    /// </summary>
    [Key(122)]
    public int CatchUpDays { get; set; } = 7;

    /// <summary>
    ///     Council 2026-10-25 (dev/PLAN_teams_knowledge.md): each team has its own recipes and codex, see
    ///     TeamKnowledge. An older host sends none: one book for everyone, as it was
    /// </summary>
    [Key(123)]
    public bool TeamKnowledge { get; set; }

    /// <summary>
    ///     Council 7, step 5: a guest's bout with an adventurer counts as the host's does, see AdventurerBout. Off: it
    ///     stays in the guest's own game. An older host sends none: off, as it was
    /// </summary>
    [Key(124)]
    public bool GuestBouts { get; set; }

    /// <summary>
    ///     Council 7, step 6: a duel with the host is fought in an arena the host makes, both come back to their tiles
    ///     after, see PlayerDuel. Off (or between two guests): where they stand
    /// </summary>
    [Key(125)]
    public bool DuelArena { get; set; }

    /// <summary>
    ///     Council 7, step 7: the challenger of a duel names a stake in gold, both put it down, the winner takes both;
    ///     given back when the duel ends without a winner (see PlayerDuel). Off: no stakes
    /// </summary>
    [Key(126)]
    public bool DuelBets { get; set; }

    /// <summary>
    ///     Each player's journal tells about that player: its message history and its own records stay its own over
    ///     world copies (OwnSettings). An older host sends none: the guest shows the host's, as it did
    /// </summary>
    [Key(127)]
    public bool OwnJournal { get; set; }

    /// <summary>
    ///     Council 2026-10-25 (dev/PLAN_teams_knowledge.md): one tax bill per team for its own lands, unpaid bills
    ///     counted per team, see TeamBills. An older host sends none: one bill for the world, as it was
    /// </summary>
    [Key(128)]
    public bool TeamBills { get; set; }

    /// <summary>
    ///     Council 2026-10-25: the furthest main-quest chapter each team took part in, on the team card, no world
    ///     effect, see TeamStory. An older host sends none: no journal
    /// </summary>
    [Key(129)]
    public bool TeamStory { get; set; }

    /// <summary>
    ///     Council 2026-10-28 (dev/COUNCIL_2026-10-28_team_story.md): each player, or each team, lives its own main
    ///     story, see TeamOwnStory. An older host sends none: one story for the world, as it was
    /// </summary>
    [Key(130)]
    public bool TeamOwnStory { get; set; }

    /// <summary>
    ///     Council 2026-10-26 (MMO world time, stage 6, "Living world"): the world's clock follows real time, a game
    ///     day per this many real minutes, not the players' turns, see LivingWorld. 0: off; an older host sends none
    /// </summary>
    [Key(131)]
    public int LivingWorldMinutes { get; set; }

    /// <summary>
    ///     With TeamOwnStory: each player, or each team, has its own place in the four guilds (membership, rank, exp,
    ///     guild quests), see TeamOwnStory.Guilds. An older host sends none: the guilds are the world's, as they were
    /// </summary>
    [Key(132)]
    public bool TeamOwnGuilds { get; set; }

    /// <summary>
    ///     Each team keeps its own bank and delivery box, see TeamBoxes. An older host sends none: the world's boxes,
    ///     as they were
    /// </summary>
    [Key(133)]
    public bool TeamBoxes { get; set; }

    /// <summary>
    ///     Each player has its own affinity with each resident, see AffinityPerPlayer. An older host sends none: one
    ///     value for the group, as it was
    /// </summary>
    [Key(135)]
    public bool AffinityPerPlayer { get; set; }

    /// <summary>
    ///     Team leaders manage their team from the Team page (<see cref="Helper.Teams" />). An older host sends none: the
    ///     choices of the player list, as they were
    /// </summary>
    [Key(136)]
    public bool TeamLeaders { get; set; }

    /// <summary>
    ///     Team leaders set a rule for each land (<see cref="Helper.Teams.Allowed" />). An older host sends none: a land
    ///     is its team's to build and others' to visit, as it was
    /// </summary>
    [Key(137)]
    public bool LandRules { get; set; }

    /// <summary>
    ///     With TeamLeaders: the most players a team holds, 0 no limit
    /// </summary>
    [Key(138)]
    public int TeamMaxSize { get; set; }

    /// <summary>
    ///     The host's message of the day and rules, read from its ElinMP/motd.txt and rules.txt (see ServerInfo)
    /// </summary>
    [Key(139)]
    public string? Motd { get; set; }

    [Key(140)]
    public string? RulesText { get; set; }

    /// <summary>
    ///     The host keeps who put or took what per container (ContainerLog). An older host sends none: no log
    /// </summary>
    [Key(142)]
    public bool ContainerLog { get; set; }

    /// <summary>
    ///     Moderators, freeze and jail (AdminCommandDelta). An older host sends none: off
    /// </summary>
    [Key(143)]
    public bool AdminExtras { get; set; }

    /// <summary>
    ///     Several lands on one world-map tile (<see cref="Helper.SiblingLands" />). An older host sends none: one land
    ///     per tile, as it was
    /// </summary>
    [Key(144)]
    public bool SiblingLands { get; set; }

    /// <summary>
    ///     Host box "Large crowds" (PLAN_100_players stage 1): each player is sent the characters near its own at full
    ///     rate, the others once a second, and a full check every 4 s. An older host sends none: everything, as it was
    /// </summary>
    [Key(148)]
    public bool LargeCrowds { get; set; }

    /// <summary>
    ///     Host box "Team character sheets" (PLAN_hub_features A). An older host sends none: no sheets
    /// </summary>
    [Key(149)]
    public bool TeamSheets { get; set; }

    /// <summary>
    ///     Host box "Activity journal" (PLAN_hub_features B). An older host sends none: no journal
    /// </summary>
    [Key(150)]
    public bool ActivityJournal { get; set; }

    /// <summary>
    ///     Host box "Team banners" (PLAN_hub_features C). An older host sends none: no banners
    /// </summary>
    [Key(151)]
    public bool TeamBanner { get; set; }

    /// <summary>
    ///     Host box "Leaderboards" (PLAN_hub_features D). An older host sends none: no rankings
    /// </summary>
    [Key(152)]
    public bool Leaderboards { get; set; }

    /// <summary>
    ///     Host box "Market" (classified ads, user 2026-10-30). An older host sends none: no market
    /// </summary>
    [Key(154)]
    public bool Market { get; set; }

    /// <summary>
    ///     Host box "Mailbox": letters between players, kept by the host. An older host sends none: no letters
    /// </summary>
    [Key(153)]
    public bool Mailbox { get; set; }

    /// <summary>
    ///     Host box "Teammates share quests": a player in a team may share its own quests with its team (TeamQuests).
    ///     Also needs teams and personal quests. An older host sends none: no sharing
    /// </summary>
    [Key(155)]
    public bool TeamQuestSharing { get; set; }

    /// <summary>
    ///     Host box "Share the host's log with bug reports": a guest's report asks the host for the end of its logs
    ///     (HostLogRequest). An older host sends none: the guest does not ask
    /// </summary>
    [Key(156)]
    public bool ShareLogForReports { get; set; }

    /// <summary>
    ///     Host box "The start land belongs to everyone" (Helper.Commons): the start meadow is nobody's, every player
    ///     builds there. An older host sends none: the start land is the host's base as before
    /// </summary>
    [Key(157)]
    public bool CommonsStartLand { get; set; }

    /// <summary>
    ///     Host setting "Mods each player may have or not" (ClientOnlyMods, comma separated): shown on the World rules
    ///     page. The handshake carries the list it applies (SourceValidationRequest). An older host sends none
    /// </summary>
    [Key(158)]
    public string? ClientOnlyMods { get; set; }

    /// <summary>
    ///     Host setting "Chat lines show as bubbles": what a player writes (the Chat tab or the game's chat box) is also a
    ///     short bubble over its character, for the players of its map. Off: no bubble over a player for its lines,
    ///     checked by each game and by the host (ChatBubbleEvent, CardRendererTalkDelta)
    /// </summary>
    [Key(159)]
    public bool ChatBubbles { get; set; } = true;

    /// <summary>
    ///     Host setting "Players can send pictures in the chat" (ChatImageDelta, ChatImages): off, the host passes none on
    /// </summary>
    [Key(160)]
    public bool ChatImages { get; set; }

    public static NetSessionRules Default => new() {
        AllowTakeover = EmpConfig.Server.Takeover.Value,
        SoftRecall = EmpConfig.Server.SoftRecall.Value,
        UseSharedTax = EmpConfig.Server.SharedTax.Value,
        OncePerPlayer = EmpConfig.Server.OncePerPlayer.Value,
        TownBoards = EmpConfig.Server.TownBoards.Value,
        PlayersAtBase = EmpConfig.Server.PlayersAtBase.Value,
        GlobalChat = EmpConfig.Server.GlobalChat.Value,
        AllowFollow = EmpConfig.Server.Follow.Value,
        PrivateChat = EmpConfig.Server.PrivateChat.Value,
        ChatBubbles = EmpConfig.Server.ChatBubbles.Value,
        ChatImages = EmpConfig.Server.ChatImages.Value,
        GuestMoongate = EmpConfig.Server.GuestMoongate.Value,
        GuestLandingPortal = EmpConfig.Server.GuestLandingPortal.Value,
        GuestSkyHelm = EmpConfig.Server.GuestSkyHelm.Value,
        SharedMarriages = EmpConfig.Server.SharedMarriages.Value,
        AffinityPerPlayer = EmpConfig.Server.AffinityPerPlayer.Value,
        UseTeams = EmpConfig.Server.Teams.Value,
        TeamBases = EmpConfig.Server.TeamBases.Value,
        TeamPersonal = EmpConfig.Server.TeamPersonal.Value,
        TeamPvp = EmpConfig.Server.TeamPvp.Value,
        TeamKnowledge = Helper.TeamKnowledge.Wanted,
        TeamBoxes = Helper.TeamBoxes.Wanted,
        ContainerLog = EmpConfig.Server.ContainerAccessLog.Value,
        AdminExtras = EmpConfig.Server.AdminExtras.Value,
        TeamLeaders = EmpConfig.Server.TeamLeaders.Value,
        LandRules = EmpConfig.Server.LandRules.Value,
        SiblingLands = EmpConfig.Server.SiblingLands.Value,
        LargeCrowds = EmpConfig.Server.LargeCrowds.Value,
        TeamMaxSize = System.Math.Max(0, EmpConfig.Server.TeamMaxSize.Value),
        TeamBills = EmpConfig.Server.TeamBills.Value,
        TeamStory = EmpConfig.Server.TeamStory.Value,
        TeamOwnStory = Helper.TeamOwnStory.Wanted,
        TeamSheets = EmpConfig.Server.TeamSheets.Value,
        ActivityJournal = EmpConfig.Server.ActivityJournal.Value,
        TeamBanner = EmpConfig.Server.TeamBanner.Value,
        Leaderboards = EmpConfig.Server.Leaderboards.Value,
        Market = EmpConfig.Server.Market.Value,
        Mailbox = EmpConfig.Server.Mailbox.Value,
        TeamQuestSharing = EmpConfig.Server.TeamQuestSharing.Value,
        ShareLogForReports = EmpConfig.Server.ShareLogForReports.Value,
        CommonsStartLand = EmpConfig.Server.CommonsStartLand.Value,
        TeamOwnGuilds = EmpConfig.Server.TeamOwnGuilds.Value,
        ClientOnlyMods = EmpConfig.Server.ClientOnlyMods.Value,
        TownLife = EmpConfig.Server.TownLife.Value,
        ShopsFromEveryPlayer = EmpConfig.Server.ShopsFromEveryPlayer.Value,
        NearestSpawns = EmpConfig.Server.NearestSpawns.Value,
        ActivitiesFromActor = EmpConfig.Server.ActivitiesFromActor.Value,
        AutoCombatPerPlayer = EmpConfig.Server.AutoCombatPerPlayer.Value,
        SummonsFollowMaster = EmpConfig.Server.SummonsFollowMaster.Value,
        // (a takeover is made from that copy: asking for one asks for the other, a host that ticked only
        // "another player takes over" got nothing)
        KeepWorldCopy = EmpConfig.Server.WorldCopy.Value || EmpConfig.Server.Takeover.Value,
        AutoResync = EmpConfig.Server.AutoResync.Value,
        UseOwnSleep = EmpConfig.Server.OwnSleep.Value,
        IdleMinutes = System.Math.Max(0, EmpConfig.Server.IdleMinutes.Value),
        NightPasses = EmpConfig.Server.NightPasses.Value,
        TimeStopsWhenEmpty = EmpConfig.Server.TimeStopsWhenEmpty.Value,
        CatchUpDays = System.Math.Max(1, EmpConfig.Server.CatchUpDays.Value),
        LivingWorldMinutes = System.Math.Max(0, EmpConfig.Server.LivingWorldMinutes.Value),
        PermadeathRevive = EmpConfig.Server.PermadeathRevive.Value,
        OwnJournal = EmpConfig.Server.OwnJournal.Value,
        TimeJumpsTogether = EmpConfig.Server.WorldMapTime.Value == WorldMapTime.TravelTogether,
        WorldMapTime = EmpConfig.Server.WorldMapTime.Value,
        DumpSparesBelt = EmpConfig.Server.DumpSparesBelt.Value,
        AllowReconnect = EmpConfig.Server.AutoReconnect.Value,
        HostManagesBase = EmpConfig.Server.HostManagesBase.Value,
        AllowDuels = EmpConfig.Server.Duels.Value,
        GuestBouts = EmpConfig.Server.GuestBouts.Value,
        DuelArena = EmpConfig.Server.DuelArena.Value,
        DuelBets = EmpConfig.Server.DuelBets.Value,
        AllowPlayerKill = EmpConfig.Server.PlayerKill.Value,
        AllowGuestBuild = EmpConfig.Server.GuestBuild.Value,
        UseSharedSpeed = EmpConfig.Server.SharedAverageSpeed.Value,
        UseTurnBasedCombat = EmpConfig.Server.TurnBasedCombat.Value,
        AllowIndependentTravel = EmpConfig.Server.IndependentTravel.Value,
        TravelCheckpointSeconds = EmpConfig.Server.TravelCheckpointSeconds.Value,
        UsePlayerShipping = EmpConfig.Server.PlayerShipping.Value,
        UsePlayerCombatTime = EmpConfig.Server.PlayerCombatTime.Value,
        UsePersonalQuests = EmpConfig.Server.PersonalQuests.Value,
        AllowPlayerTrade = EmpConfig.Server.PlayerTrade.Value,
        UsePlayerClock = EmpConfig.Server.PlayerClock.Value,
        UsePlayerStepPace = EmpConfig.Server.PlayerStepPace.Value,
        UseSharedWorldTime = EmpConfig.Server.SharedWorldTime.Value,
        UseWorldKeeper = EmpConfig.Server.WorldKeeper.Value,
        Motd = Helper.ServerInfo.ReadFile("motd.txt"),
        RulesText = Helper.ServerInfo.ReadFile("rules.txt"),
    };
}

/// <summary>
///     Council 2026-10-23, one choice of the host: who pays the hours a trip on the world map costs. <br />
///     SharedClock: the world map is a map like any other, every step of anyone moves the date of the game that
///     took it, the most advanced date is the world's and the others catch up without living it. <br />
///     TravelTogether: a step moves the date only when every player is on the world map, whoever steps. <br />
///     PersonalPace: as SharedClock, except that the world map hours of a player travelling while the others are
///     elsewhere are held back as that player's lead (its hunger and its own quests pay them, the date stays), up
///     to the host's cap; the world catches up with the largest lead once everyone is on the world map together or
///     sleeps the same night, see ElinNetHost.HoldLead. An older peer reads it as SharedClock
/// </summary>
public enum WorldMapTime
{
    SharedClock,
    TravelTogether,
    PersonalPace,
}

/// <summary>
///     Council 2026-10-26, "Night passes when": the share of online, active players asleep that makes the night
///     of the world. Away-from-keyboard and dead players are not counted
/// </summary>
public enum NightRule
{
    Everyone,
    Half,
    One,
}
