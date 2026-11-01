using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ElinTogether.API.SourceValidation;
using ElinTogether.LangMod;
using ElinTogether.Net;
using ElinTogether.Net.Steam;

namespace ElinTogether.Components;

/// <summary>
///     Where a reader takes the values of the host's rules from: the config entries themselves, their defaults, a
///     preset, or the rules a guest received from its host. Null: not known there (a guest is not told every rule)
/// </summary>
internal sealed class RuleSource(Func<ConfigEntryBase, object?> read)
{
    internal static RuleSource Current => new(e => e.BoxedValue);
    internal static RuleSource Defaults => new(e => e.DefaultValue);

    internal bool? Get(ConfigEntryBase entry) => read(entry) as bool?;
    internal object? Raw(ConfigEntryBase entry) => read(entry);
}

/// <summary>
///     One question a host answers, written as several of the old boxes (council 2026-10-23): the boxes stay the
///     storage, a choice only reads them (Read, -1 when they match no option: "Custom (see Advanced)") and writes
///     them when the host picks an option. It never writes on open, nor for "Custom"
/// </summary>
internal sealed class Choice(string id, string[] options, Func<ConfigEntryBase[]> entries, Func<RuleSource, int> read,
    Func<int, (ConfigEntryBase Entry, object Value)[]> write)
{
    internal string Id => id;
    internal string[] Options => options;
    internal ConfigEntryBase[] Entries => entries();
    internal int Read(RuleSource source) => read(source);

    /// <summary>
    ///     What option i writes; nothing for an index past the options (the "Custom" line of the dropdown)
    /// </summary>
    internal (ConfigEntryBase Entry, object Value)[] WriteFor(int i) => i >= 0 && i < options.Length ? write(i) : [];

    internal string Label => $"emp_ui_sv_cfg_{id}".lang();
    internal string OptionLabel(int i) => i >= 0 && i < options.Length ? $"emp_ui_sv_opt_{id}_{options[i]}".lang() : "emp_ui_sv_custom".lang();
}

/// <summary>
///     A line of the host's rules. Entry: a box; Choice: a question over several boxes; neither: a line drawn by the
///     renderer itself (the joining checks, the tax bills). Under: the choice whose boxes these are, drawn indented
///     under it in Advanced. Requires: false greys the box out (what it needs is off). Apply: when a change counts,
///     for the rules that do not apply at once
/// </summary>
internal sealed record SettingRow(string Id, string Card, bool Advanced, ConfigEntry<bool>? Entry = null, Choice? Choice = null,
    Func<bool>? Requires = null, string? Apply = null, string? Under = null)
{
    internal string Label => Choice?.Label ?? $"emp_ui_sv_cfg_{Id}".lang();

    internal string Description
    {
        get {
            var desc = $"emp_ui_sv_desc_{Id}".lang();
            // (under a dropdown, one line per choice: the applies-when line comes on a line of its own, last)
            // (the official server's rule, said where the box is greyed)
            if (Id == "import_chara" && SettingsTable.OfficialLocked) {
                desc = $"{desc} {"emp_ui_sv_import_official".lang()}";
            }

            if (Id is "team_own_story" or "team_knowledge" && SettingsTable.OfficialLocked) {
                desc = $"{desc} {"emp_ui_sv_own_story_official".lang()}";
            }

            return Apply is null ? desc : $"{desc}{(Choice is null ? " " : "\n")}{$"emp_ui_sv_apply_{Apply}".lang()}";
        }
    }
}

/// <summary>
///     The host's rules as one table, drawn by the mod's own window (TabServerConfiguration) and by the game's mod
///     config page (ModConfigPage) alike. Entries are read each time (EmpConfig.Reset binds new ones)
/// </summary>
internal static class SettingsTable
{
    // WHERE A NEW OPTION GOES (council 2026-10-23, placement rule):
    // - a new option goes to Advanced (Advanced: true), in the group that fits it;
    // - it is visible only if a new host must decide it before friends join, and then by joining an existing
    //   choice (a new option of a Choice) or by replacing a visible row: never more than 13 visible rows;
    // - a box that only brings back the old host-centred behaviour always goes to the "fallbacks" group;
    // - its label is a sentence about what the players experience, says "host" and "friends" or "players",
    //   never "guest"; its texts come in English, Japanese and Chinese in the same commit;
    // - nothing is ever removed: a hidden box keeps its stored value and stays in Advanced.
    internal static readonly string[] Cards = ["joining", "together", "host_leaves", "friends", "fallbacks", "time_details", "comfort", "repair"];

    // the boxes of the host-centred fallbacks: "every player gets their own" on is all of them on
    private static ConfigEntry<bool>[] Fallbacks() => [
        EmpConfig.Server.PersonalQuests, EmpConfig.Server.PlayerShipping, EmpConfig.Server.OncePerPlayer,
        EmpConfig.Server.SharedTax, EmpConfig.Server.TownLife, EmpConfig.Server.TownBoards,
        EmpConfig.Server.ShopsFromEveryPlayer, EmpConfig.Server.NearestSpawns, EmpConfig.Server.ActivitiesFromActor,
        EmpConfig.Server.AutoCombatPerPlayer, EmpConfig.Server.PlayersAtBase, EmpConfig.Server.SummonsFollowMaster,
        EmpConfig.Server.GlobalChat, EmpConfig.Server.OwnJournal,
    ];

    internal static Choice WhoJoins => new("who_joins", ["anyone", "mods", "data", "both"],
        () => [EmpConfig.Server.SourceValidationSet],
        source => {
            if (source.Raw(EmpConfig.Server.SourceValidationSet) is not string raw || raw.Contains(':')) {
                return -1;
            }

            // (the old "All" box also checks game files: stronger than "both", so it reads as Custom and picking an
            // option never drops the file checks silently; review 2026-10-23)
            return ValidationConfig.GetConfiguredFlags(raw) switch {
                ValidationFlags.None => 0,
                ValidationFlags.Plugins => 1,
                ValidationFlags.Sources => 2,
                ValidationFlags.Sources | ValidationFlags.Plugins => 3,
                _ => -1,
            };
        },
        i => [(EmpConfig.Server.SourceValidationSet, new[] { "", "plugins", "sources", "sources,plugins" }[i])]);

    // (council 2026-10-25 settings trim: one question where there were two boxes; choose off with import on reads Custom)
    internal static Choice Characters => Pair("characters", ["new", "pick", "both"],
        EmpConfig.Server.ChooseCharacter, EmpConfig.Server.ImportCharacter, [(false, false), (true, false), (true, true)]);

    // together: nobody travels alone (the world map clock is left as it is, it means nothing then); a world map clock
    // "travel together" with independent travel on reads Custom
    internal static Choice Travel => new("travel", ["together", "alone", "own_pace"],
        () => [EmpConfig.Server.IndependentTravel, EmpConfig.Server.WorldMapTime],
        source => (source.Get(EmpConfig.Server.IndependentTravel), source.Raw(EmpConfig.Server.WorldMapTime)) switch {
            (false, _) => 0,
            (true, Net.WorldMapTime.SharedClock) => 1,
            (true, Net.WorldMapTime.PersonalPace) => 2,
            _ => -1,
        },
        i => i switch {
            0 => [(EmpConfig.Server.IndependentTravel, false)],
            1 => [(EmpConfig.Server.IndependentTravel, true), (EmpConfig.Server.WorldMapTime, Net.WorldMapTime.SharedClock)],
            _ => [(EmpConfig.Server.IndependentTravel, true), (EmpConfig.Server.WorldMapTime, Net.WorldMapTime.PersonalPace)],
        });

    // where a new player of a world with teams appears (another zone id typed in the file reads as Custom and is kept)
    internal static Choice SpawnZone => new("spawn_zone", ["start", "host"],
        () => [EmpConfig.Server.SpawnZone],
        source => source.Raw(EmpConfig.Server.SpawnZone) is string raw ? System.Array.IndexOf(new[] { "start", "host" }, raw) : -1,
        i => [(EmpConfig.Server.SpawnZone, new[] { "start", "host" }[i])]);

    internal static Choice WorldMapTime => new("world_map_choice", ["SharedClock", "TravelTogether", "PersonalPace"],
        () => [EmpConfig.Server.WorldMapTime],
        source => source.Raw(EmpConfig.Server.WorldMapTime) is Net.WorldMapTime mode ? (int)mode : -1,
        i => [(EmpConfig.Server.WorldMapTime, (Net.WorldMapTime)i)]);

    // Personal Pace: the hours a player travelling alone may run ahead of the world (another number typed in the
    // file reads as Custom and is kept)
    private static readonly int[] LeadCaps = [12, 24, 48, 72];

    internal static Choice LeadCap => new("world_map_lead", [..LeadCaps.Select(h => h.ToString())],
        () => [EmpConfig.Server.WorldMapLeadCap],
        source => source.Raw(EmpConfig.Server.WorldMapLeadCap) is int hours ? Array.IndexOf(LeadCaps, hours) : -1,
        i => [(EmpConfig.Server.WorldMapLeadCap, LeadCaps[i])]);

    // council 2026-10-26: how many of the active players must sleep for the night of the world
    internal static Choice NightPasses => new("night_passes", ["Everyone", "Half", "One"],
        () => [EmpConfig.Server.NightPasses],
        source => source.Raw(EmpConfig.Server.NightPasses) is NightRule rule ? (int)rule : -1,
        i => [(EmpConfig.Server.NightPasses, (NightRule)i)]);

    // minutes without input before a player is away from the keyboard, 0 off (another number typed in the file
    // reads as Custom and is kept)
    private static readonly int[] IdleSteps = [0, 5, 15, 30];

    // minutes a kicked player stays out (admin tools, council 2026-10-27)
    private static readonly int[] KeepOutSteps = [0, 5, 15, 60];

    internal static Choice KickKeepOut => new("kick_keep_out", [..KeepOutSteps.Select(m => m.ToString())],
        () => [EmpConfig.Server.KickKeepOutMinutes],
        source => source.Raw(EmpConfig.Server.KickKeepOutMinutes) is int minutes ? Array.IndexOf(KeepOutSteps, minutes) : -1,
        i => [(EmpConfig.Server.KickKeepOutMinutes, KeepOutSteps[i])]);

    // players per team and minutes before founding another (council 2026-10-28); another number typed in the file
    // reads as Custom and is kept
    private static readonly int[] TeamSizeSteps = [5, 10, 20, 50, 0];
    private static readonly int[] FoundWaitSteps = [0, 5, 30, 60];

    internal static Choice TeamMaxSize => new("team_max_size", [..TeamSizeSteps.Select(n => n.ToString())],
        () => [EmpConfig.Server.TeamMaxSize],
        source => source.Raw(EmpConfig.Server.TeamMaxSize) is int count ? Array.IndexOf(TeamSizeSteps, count) : -1,
        i => [(EmpConfig.Server.TeamMaxSize, TeamSizeSteps[i])]);

    internal static Choice TeamFoundCooldown => new("team_found_cooldown", [..FoundWaitSteps.Select(n => n.ToString())],
        () => [EmpConfig.Server.TeamFoundCooldown],
        source => source.Raw(EmpConfig.Server.TeamFoundCooldown) is int minutes ? Array.IndexOf(FoundWaitSteps, minutes) : -1,
        i => [(EmpConfig.Server.TeamFoundCooldown, FoundWaitSteps[i])]);

    // council 2026-10-28: real days away before a team loses its lands, and lands one team may claim (0 no limit)
    private static readonly int[] AbandonSteps = [7, 14, 30, 60, 90];
    private static readonly int[] LandCapSteps = [1, 2, 3, 5, 10, 0];

    internal static Choice AbandonDays => new("abandon_days", [..AbandonSteps.Select(n => n.ToString())],
        () => [EmpConfig.Server.AbandonDays],
        source => source.Raw(EmpConfig.Server.AbandonDays) is int days ? Array.IndexOf(AbandonSteps, days) : -1,
        i => [(EmpConfig.Server.AbandonDays, AbandonSteps[i])]);

    internal static Choice LandsPerTeam => new("lands_per_team", [..LandCapSteps.Select(n => n.ToString())],
        () => [EmpConfig.Server.LandsPerTeam],
        source => source.Raw(EmpConfig.Server.LandsPerTeam) is int count ? Array.IndexOf(LandCapSteps, count) : -1,
        i => [(EmpConfig.Server.LandsPerTeam, LandCapSteps[i])]);

    // characters per player (user, 2026-10-27), 0 no limit
    private static readonly int[] CharaSteps = [1, 3, 5, 10, 0];

    internal static Choice MaxCharacters => new("max_characters", [..CharaSteps.Select(n => n.ToString())],
        () => [EmpConfig.Server.MaxCharacters],
        source => source.Raw(EmpConfig.Server.MaxCharacters) is int count ? Array.IndexOf(CharaSteps, count) : -1,
        i => [(EmpConfig.Server.MaxCharacters, CharaSteps[i])]);

    // council 2026-10-26 (stage 5): the last days a long catch-up lives (another number typed in the file reads as
    // Custom and is kept)
    private static readonly int[] CatchUpSteps = [1, 3, 7];

    internal static Choice CatchUpDays => new("catch_up_days", [..CatchUpSteps.Select(d => d.ToString())],
        () => [EmpConfig.Server.CatchUpDays],
        source => source.Raw(EmpConfig.Server.CatchUpDays) is int days ? Array.IndexOf(CatchUpSteps, days) : -1,
        i => [(EmpConfig.Server.CatchUpDays, CatchUpSteps[i])]);

    // council 2026-10-26 (stage 6, Living world): real minutes per game day, 0 off (another number typed in the file
    // reads as Custom and is kept)
    private static readonly int[] LivingSteps = [0, 20, 40, 60, 120];

    internal static Choice LivingWorld => new("living_world", [..LivingSteps.Select(m => m.ToString())],
        () => [EmpConfig.Server.LivingWorldMinutes],
        source => source.Raw(EmpConfig.Server.LivingWorldMinutes) is int minutes ? Array.IndexOf(LivingSteps, minutes) : -1,
        i => [(EmpConfig.Server.LivingWorldMinutes, LivingSteps[i])]);

    internal static Choice IdleMinutes => new("idle_minutes", [..IdleSteps.Select(m => m.ToString())],
        () => [EmpConfig.Server.IdleMinutes],
        source => source.Raw(EmpConfig.Server.IdleMinutes) is int minutes ? Array.IndexOf(IdleSteps, minutes) : -1,
        i => [(EmpConfig.Server.IdleMinutes, IdleSteps[i])]);

    // own pace: PlayerCombatTime with its clock and step pace (turn-based does nothing then); turn-based, shared
    // average and classic: without PlayerCombatTime (clock and step pace do nothing then, they are left alone)
    internal static Choice CombatPace => new("combat_pace", ["own", "turns", "average", "classic"],
        () => [EmpConfig.Server.PlayerCombatTime, EmpConfig.Server.PlayerClock, EmpConfig.Server.PlayerStepPace,
            EmpConfig.Server.TurnBasedCombat, EmpConfig.Server.SharedAverageSpeed],
        source => {
            var (pct, pc, psp, tb, sas) = (source.Get(EmpConfig.Server.PlayerCombatTime), source.Get(EmpConfig.Server.PlayerClock),
                source.Get(EmpConfig.Server.PlayerStepPace), source.Get(EmpConfig.Server.TurnBasedCombat),
                source.Get(EmpConfig.Server.SharedAverageSpeed));
            return (pct, pc, psp, tb, sas) switch {
                (true, true, true, _, false) => 0,
                (false, _, _, true, false) => 1,
                (false, _, _, false, true) => 2,
                (false, _, _, false, false) => 3,
                _ => -1,
            };
        },
        i => i switch {
            0 => [(EmpConfig.Server.PlayerCombatTime, true), (EmpConfig.Server.PlayerClock, true),
                (EmpConfig.Server.PlayerStepPace, true), (EmpConfig.Server.SharedAverageSpeed, false)],
            1 => [(EmpConfig.Server.PlayerCombatTime, false), (EmpConfig.Server.TurnBasedCombat, true),
                (EmpConfig.Server.SharedAverageSpeed, false)],
            2 => [(EmpConfig.Server.PlayerCombatTime, false), (EmpConfig.Server.TurnBasedCombat, false),
                (EmpConfig.Server.SharedAverageSpeed, true)],
            _ => [(EmpConfig.Server.PlayerCombatTime, false), (EmpConfig.Server.TurnBasedCombat, false),
                (EmpConfig.Server.SharedAverageSpeed, false)],
        });

    // (a guest is told only some of the fallbacks: the ones it knows decide)
    internal static Choice OwnEverything => new("own_everything", ["yes", "no"],
        () => [..Fallbacks()],
        source => {
            var known = Fallbacks().Select(e => source.Get(e)).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return known.Length == 0 ? -1 : known.All(v => v) ? 0 : known.All(v => !v) ? 1 : -1;
        },
        i => [..Fallbacks().Select(e => ((ConfigEntryBase)e, (object)(i == 0)))]);

    // (the base's management also covers the build mode: "everyone builds, the host manages" has no boxes)
    internal static Choice Building => Pair("building", ["all", "manage", "host"],
        EmpConfig.Server.GuestBuild, EmpConfig.Server.HostManagesBase, [(true, false), (false, false), (false, true)]);

    internal static Choice Fights => Pair("fights", ["off", "duels", "kills"],
        EmpConfig.Server.Duels, EmpConfig.Server.PlayerKill, [(false, false), (true, false), (true, true)]);

    internal static Choice HostLeaves => Pair("host_leaves", ["waits", "backup", "carry_on"],
        EmpConfig.Server.WorldCopy, EmpConfig.Server.Takeover, [(false, false), (true, false), (true, true)]);

    private static Choice Pair(string id, string[] options, ConfigEntry<bool> a, ConfigEntry<bool> b, (bool A, bool B)[] values)
    {
        return new(id, options, () => [a, b],
            source => source.Get(a) is { } va && source.Get(b) is { } vb ? Array.IndexOf(values, (va, vb)) : -1,
            i => [(a, values[i].A), (b, values[i].B)]);
    }

    /// <summary>
    ///     The box "Play as a guest of a hidden server" exists on Windows only (HiddenServer)
    /// </summary>
    internal static bool HiddenServerAvailable => UnityEngine.Application.platform is UnityEngine.RuntimePlatform.WindowsPlayer
        or UnityEngine.RuntimePlatform.WindowsEditor;

    internal static IEnumerable<Choice> Choices() => Rows().Where(r => r.Choice is not null).Select(r => r.Choice!);

    /// <summary>
    ///     The table, with the rows the official server fixes greyed out while this game hosts it
    /// </summary>
    /// <summary>
    ///     The world's rules in one line for a bug report (user 2026-10-30): the ones that differ from the defaults,
    ///     "id=on/off" or "id=option". A guest reads what its host told; the host its own config
    /// </summary>
    internal static string ReportSummary()
    {
        try {
            var src = Net.NetSession.Instance.Transport is Net.ElinNetClient ? FromRules(Net.NetSession.Instance.Rules) : RuleSource.Current;
            var parts = new List<string>();
            foreach (var row in RowsRaw()) {
                if (row.Choice is { } c) {
                    var now = c.Read(src);
                    if (now >= 0 && now != c.Read(RuleSource.Defaults)) {
                        parts.Add($"{row.Id}={(now < c.Options.Length ? c.Options[now] : now.ToString())}");
                    }
                } else if (row.Entry is { } e && src.Get(e) is { } on && on != (bool)e.DefaultValue) {
                    parts.Add($"{row.Id}={(on ? "on" : "off")}");
                }
            }

            return parts.Count == 0 ? "all at their defaults" : string.Join(", ", parts);
        } catch (System.Exception ex) {
            return "unreadable (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>
    ///     The official server's fixed rules are locked (greyed rows, skipped by a preset, a reset or an undo) only while
    ///     this game really runs it (-empserver with the account and the key) or the bench forces it. Not merely because
    ///     PublicGame is on with the key on this PC: PublicGame is itself a fixed rule, so a host that picked the public
    ///     server preset could never go back to Recommended
    /// </summary>
    internal static bool OfficialLocked =>
        (OfficialServer.BenchForce || EmpServer.Requested && OfficialServer.CanHost()) && !Helper.PeerHosting.Active;

    internal static IEnumerable<SettingRow> Rows()
    {
        var fixedEntries = OfficialFixedEntries();
        foreach (var row in RowsRaw()) {
            if (row.Entry is { } e && fixedEntries.Contains(e) || row.Choice is { } c && c.Entries.All(fixedEntries.Contains)) {
                var own = row.Requires;
                yield return row with { Requires = () => !OfficialLocked && (own?.Invoke() ?? true) };
            } else {
                yield return row;
            }
        }
    }

    private static IEnumerable<SettingRow> RowsRaw()
    {
        var pct = EmpConfig.Server.PlayerCombatTime;

        // Joining
        yield return new("who_joins", "joining", false, Choice: WhoJoins, Apply: "join");
        yield return new("checks", "joining", true, Under: "who_joins", Apply: "join");
        // (a text row, drawn by the page itself: the mods each player may have or not)
        yield return new("client_mods", "joining", true, Under: "who_joins", Apply: "join");
        yield return new("same_game_version", "joining", true, EmpConfig.Server.SameGameVersion, Apply: "join");
        yield return new("characters", "joining", false, Choice: Characters, Apply: "join");
        yield return new("choose_chara", "joining", true, EmpConfig.Server.ChooseCharacter, Apply: "join", Under: "characters");
        yield return new("import_chara", "joining", true, EmpConfig.Server.ImportCharacter, Requires: () => !OfficialLocked, Apply: "join", Under: "characters");
        // (the way back is the import: without it a character that left could only be brought back unchanged)
        yield return new("export_chara", "joining", true, EmpConfig.Server.ExportCharacter,
            Requires: () => EmpConfig.Server.ImportCharacter.Value && !OfficialLocked, Apply: "join", Under: "characters");
        yield return new("permadeath_revive", "joining", true, EmpConfig.Server.PermadeathRevive);

        // Playing together
        yield return new("travel", "together", false, Choice: Travel);
        yield return new("independent_travel", "together", true, EmpConfig.Server.IndependentTravel, Under: "travel");
        yield return new("world_map_choice", "together", true, Choice: WorldMapTime, Under: "travel");
        yield return new("combat_pace", "together", false, Choice: CombatPace);
        yield return new("combat_time", "together", true, pct, Under: "combat_pace");
        yield return new("player_clock", "together", true, EmpConfig.Server.PlayerClock, Requires: () => pct.Value, Under: "combat_pace");
        yield return new("step_pace", "together", true, EmpConfig.Server.PlayerStepPace, Requires: () => pct.Value, Under: "combat_pace");
        yield return new("turn_combat", "together", true, EmpConfig.Server.TurnBasedCombat, Requires: () => !pct.Value, Under: "combat_pace");
        yield return new("shared_speed", "together", true, EmpConfig.Server.SharedAverageSpeed, Under: "combat_pace");
        yield return new("own_everything", "fallbacks", true, Choice: OwnEverything);
        yield return new("building", "together", false, Choice: Building);
        yield return new("guest_build", "together", true, EmpConfig.Server.GuestBuild, Under: "building");
        yield return new("host_manages_base", "together", true, EmpConfig.Server.HostManagesBase, Under: "building");
        yield return new("fights", "together", false, Choice: Fights);
        yield return new("duels", "together", true, EmpConfig.Server.Duels, Under: "fights");
        yield return new("player_kill", "together", true, EmpConfig.Server.PlayerKill, Under: "fights");
        yield return new("guest_bouts", "together", true, EmpConfig.Server.GuestBouts);
        yield return new("duel_arena", "together", true, EmpConfig.Server.DuelArena, Requires: () => EmpConfig.Server.Duels.Value);
        yield return new("duel_bets", "together", true, EmpConfig.Server.DuelBets, Requires: () => EmpConfig.Server.Duels.Value);
        yield return new("player_trade", "friends", true, EmpConfig.Server.PlayerTrade);
        yield return new("follow", "friends", true, EmpConfig.Server.Follow);
        yield return new("private_chat", "friends", true, EmpConfig.Server.PrivateChat);
        yield return new("guest_moongate", "friends", true, EmpConfig.Server.GuestMoongate);
        yield return new("stranger_visits", "friends", true, EmpConfig.Server.StrangerVisits);
        yield return new("guest_landing_portal", "friends", true, EmpConfig.Server.GuestLandingPortal);
        yield return new("guest_sky_helm", "friends", true, EmpConfig.Server.GuestSkyHelm);
        yield return new("shared_marriages", "friends", true, EmpConfig.Server.SharedMarriages);
        yield return new("affinity_per_player", "friends", true, EmpConfig.Server.AffinityPerPlayer);
        yield return new("public_game", "joining", true, EmpConfig.Server.PublicGame);
        yield return new("kick_keep_out", "joining", true, Choice: KickKeepOut);
        yield return new("members_only", "joining", true, EmpConfig.Server.MembersOnly);
        yield return new("admin_extras", "joining", true, EmpConfig.Server.AdminExtras);
        yield return new("container_log", "together", true, EmpConfig.Server.ContainerAccessLog);
        yield return new("max_characters", "joining", true, Choice: MaxCharacters);
        yield return new("teams", "together", true, EmpConfig.Server.Teams);
        yield return new("team_bases", "together", true, EmpConfig.Server.TeamBases, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_approval", "together", true, EmpConfig.Server.TeamApproval, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_deed", "together", true, EmpConfig.Server.TeamDeed, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("land_rules", "together", true, EmpConfig.Server.LandRules, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamBases.Value);
        yield return new("commons_start_land", "together", true, EmpConfig.Server.CommonsStartLand);
        yield return new("sibling_lands", "together", true, EmpConfig.Server.SiblingLands, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamBases.Value);
        yield return new("land_abandon", "together", true, EmpConfig.Server.LandAbandon, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamBases.Value);
        yield return new("abandon_days", "together", true, Choice: AbandonDays, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamBases.Value && EmpConfig.Server.LandAbandon.Value);
        yield return new("lands_per_team", "together", true, Choice: LandsPerTeam, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamBases.Value);
        yield return new("team_leaders", "together", true, EmpConfig.Server.TeamLeaders, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_max_size", "together", true, Choice: TeamMaxSize, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamLeaders.Value);
        yield return new("team_found_cooldown", "together", true, Choice: TeamFoundCooldown, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamLeaders.Value);
        yield return new("team_personal", "together", true, EmpConfig.Server.TeamPersonal, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("spawn_zone", "together", true, Choice: SpawnZone, Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.IndependentTravel.Value);
        yield return new("team_pvp", "together", true, EmpConfig.Server.TeamPvp, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_knowledge", "together", true, EmpConfig.Server.TeamKnowledge, Requires: () => !OfficialLocked);
        yield return new("team_boxes", "together", true, EmpConfig.Server.TeamBoxes, Requires: () => EmpConfig.Server.Teams.Value && !OfficialLocked);
        yield return new("team_bills", "together", true, EmpConfig.Server.TeamBills, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_story", "together", true, EmpConfig.Server.TeamStory, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_sheets", "together", true, EmpConfig.Server.TeamSheets, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("team_quest_sharing", "together", true, EmpConfig.Server.TeamQuestSharing,
            Requires: () => EmpConfig.Server.Teams.Value && EmpConfig.Server.PersonalQuests.Value);
        yield return new("activity_journal", "together", true, EmpConfig.Server.ActivityJournal);
        yield return new("team_banner", "together", true, EmpConfig.Server.TeamBanner, Requires: () => EmpConfig.Server.Teams.Value);
        yield return new("leaderboards", "together", true, EmpConfig.Server.Leaderboards);
        yield return new("market", "together", true, EmpConfig.Server.Market);
        yield return new("mailbox", "together", true, EmpConfig.Server.Mailbox);
        yield return new("team_own_story", "together", true, EmpConfig.Server.TeamOwnStory, Requires: () => !OfficialLocked);
        yield return new("team_own_guilds", "together", true, EmpConfig.Server.TeamOwnGuilds, Requires: () => Helper.TeamOwnStory.Wanted);

        // When the host leaves
        yield return new("host_leaves", "host_leaves", false, Choice: HostLeaves);
        yield return new("world_copy", "host_leaves", true, EmpConfig.Server.WorldCopy, Under: "host_leaves");
        yield return new("takeover", "host_leaves", true, EmpConfig.Server.Takeover, Requires: () => EmpConfig.Server.WorldCopy.Value,
            Under: "host_leaves");
        yield return new("auto_host", "host_leaves", true, EmpConfig.Server.AutoHost, Apply: "load");
        // (Windows only: the hidden server needs the single-instance mutex of Windows; it is the player's own choice,
        // not a rule of the world: no preset, reset or undo touches it, see HostRules)
        if (HiddenServerAvailable) {
            yield return new("hidden_server", "host_leaves", true, EmpConfig.Server.HiddenServer);
        }

        // Advanced: the way back to the host-centred game, one box each
        yield return new("personal_quests", "fallbacks", true, EmpConfig.Server.PersonalQuests);
        yield return new("player_shipping", "fallbacks", true, EmpConfig.Server.PlayerShipping);
        yield return new("once_per_player", "fallbacks", true, EmpConfig.Server.OncePerPlayer);
        yield return new("shared_tax", "fallbacks", true, EmpConfig.Server.SharedTax);
        yield return new("town_life", "fallbacks", true, EmpConfig.Server.TownLife);
        yield return new("town_boards", "fallbacks", true, EmpConfig.Server.TownBoards, Requires: () => EmpConfig.Server.PersonalQuests.Value);
        yield return new("shops_from_every_player", "fallbacks", true, EmpConfig.Server.ShopsFromEveryPlayer);
        yield return new("nearest_spawns", "fallbacks", true, EmpConfig.Server.NearestSpawns);
        yield return new("activities_from_actor", "fallbacks", true, EmpConfig.Server.ActivitiesFromActor);
        yield return new("auto_combat_per_player", "fallbacks", true, EmpConfig.Server.AutoCombatPerPlayer);
        yield return new("players_at_base", "fallbacks", true, EmpConfig.Server.PlayersAtBase);
        yield return new("summons_follow_master", "fallbacks", true, EmpConfig.Server.SummonsFollowMaster);
        yield return new("global_chat", "fallbacks", true, EmpConfig.Server.GlobalChat);
        yield return new("own_journal", "fallbacks", true, EmpConfig.Server.OwnJournal);

        yield return new("world_time", "time_details", true, EmpConfig.Server.SharedWorldTime);
        yield return new("world_keeper", "time_details", true, EmpConfig.Server.WorldKeeper);
        yield return new("own_sleep", "time_details", true, EmpConfig.Server.OwnSleep);
        yield return new("night_passes", "time_details", true, Choice: NightPasses, Requires: () => EmpConfig.Server.OwnSleep.Value);
        yield return new("idle_minutes", "time_details", true, Choice: IdleMinutes);
        yield return new("time_stops_empty", "time_details", true, EmpConfig.Server.TimeStopsWhenEmpty);
        yield return new("living_world", "time_details", true, Choice: LivingWorld, Requires: () => EmpConfig.Server.SharedWorldTime.Value);
        yield return new("absent_quests_wait", "time_details", true, EmpConfig.Server.AbsentQuestsWait, Requires: () => EmpConfig.Server.PersonalQuests.Value);
        yield return new("catch_up_days", "time_details", true, Choice: CatchUpDays);
        yield return new("world_map_lead", "time_details", true, Choice: LeadCap, Requires: () => EmpConfig.Server.WorldMapTime.Value == Net.WorldMapTime.PersonalPace);

        yield return new("delete_chara", "comfort", true, EmpConfig.Server.DeleteCharacter, Apply: "join");
        yield return new("dump_spares_belt", "comfort", true, EmpConfig.Server.DumpSparesBelt);
        yield return new("publish_mods", "comfort", true, EmpConfig.Server.PublishMods, Apply: "join");
        yield return new("look_mods_own", "comfort", true, EmpConfig.Server.LookModsOwn, Apply: "join", Requires: () => EmpConfig.Server.PublishMods.Value);

        yield return new("auto_reconnect", "repair", true, EmpConfig.Server.AutoReconnect);
        yield return new("auto_resync", "repair", true, EmpConfig.Server.AutoResync);
        yield return new("soft_recall", "repair", true, EmpConfig.Server.SoftRecall);
        yield return new("auto_save", "repair", true, EmpConfig.Server.AutoSave);
        yield return new("server_rests", "repair", true, EmpConfig.Server.ServerRestsWhenEmpty);
        yield return new("large_crowds", "repair", true, EmpConfig.Server.LargeCrowds);
        yield return new("fast_joins", "repair", true, EmpConfig.Server.FastJoinsAndSaves);
        yield return new("server_avatar", "repair", true, EmpConfig.Server.ServerAvatar, Apply: "restart");
        yield return new("enforce_ownership", "repair", true, EmpConfig.Server.EnforceOwnership);
        yield return new("share_log_reports", "repair", true, EmpConfig.Server.ShareLogForReports);
        yield return new("bills", "repair", true);
    }

    /// <summary>
    ///     Every stored host rule the table shows: what a preset, the reset and the undo touch. Not the client's
    ///     own options, the depot, SoftRecallSwitchedOn or the keys only in the file: TravelCheckpointSeconds and
    ///     StrictValidationMode are file-only on purpose (a tuning value and a hardening switch, not a choice for the page)
    /// </summary>
    internal static ConfigEntryBase[] HostRules() => [
        ..Rows().Where(r => r.Entry is not null && r.Id != "hidden_server").Select(r => (ConfigEntryBase)r.Entry!).Distinct(),
        EmpConfig.Server.SourceValidationSet, EmpConfig.Server.WorldMapTime, EmpConfig.Server.WorldMapLeadCap,
        EmpConfig.Server.SpawnZone, EmpConfig.Server.NightPasses, EmpConfig.Server.IdleMinutes,
        EmpConfig.Server.KickKeepOutMinutes, EmpConfig.Server.MaxCharacters, EmpConfig.Server.CatchUpDays,
        EmpConfig.Server.LivingWorldMinutes, EmpConfig.Server.TeamMaxSize, EmpConfig.Server.TeamFoundCooldown,
        EmpConfig.Server.AbandonDays, EmpConfig.Server.LandsPerTeam, EmpConfig.Server.ClientOnlyMods,
    ];

    // presets: computed from the boxes each time a page opens, never stored (an old world with one box of its
    // own reads as Custom, with what differs listed, and stays as it is). Only names are kept anywhere: every page
    // finds an index with PresetIndex. Lead dev 2026-11-01: "Classic host-led" has no card any more (its rules stay
    // under Advanced, a world still on them reads as Custom); Duo, Hardcore and Big group were added
    internal static readonly string[] Presets = ["recommended", "duo", "hardcore", "community", "official", "custom"];

    internal static int PresetIndex(string name) => Array.IndexOf(Presets, name);

    internal static readonly int CustomPreset = PresetIndex("custom");
    internal static readonly int OfficialPreset = PresetIndex("official");

    /// <summary>
    ///     A preset as the defaults with these values written over them
    /// </summary>
    private static RuleSource Over(IEnumerable<(ConfigEntryBase Entry, object Value)> writes)
    {
        var values = new Dictionary<ConfigEntryBase, object>();
        foreach (var (entry, value) in writes) {
            values[entry] = value;
        }

        return new(e => values.TryGetValue(e, out var v) ? v : e.DefaultValue);
    }

    /// <summary>
    ///     The old "Classic host-led" set (no card since 2026-11-01): the fallbacks off, turn-based combat, everyone
    ///     follows the host, only the host builds and manages the base. Kept for the check that such a world reads Custom
    /// </summary>
    internal static RuleSource Classic => Over(CombatPace.WriteFor(1).Concat(OwnEverything.WriteFor(1)).Concat(Building.WriteFor(2))
        .Concat(Travel.WriteFor(0)));

    /// <summary>
    ///     Duo / small group (2-4 friends): Recommended, but everyone travels together, so a moongate or a landing
    ///     portal never takes one player away alone. One story, one base and its chests for all, no teams, no PvP
    /// </summary>
    internal static RuleSource Duo => Over(Travel.WriteFor(0).Concat([
        (EmpConfig.Server.GuestMoongate, (object)false), (EmpConfig.Server.GuestLandingPortal, false),
    ]));

    /// <summary>
    ///     Hardcore: Recommended with players allowed to kill each other, one character per player, no character
    ///     brought from a solo save or taken back to one, and a dead player of a permadeath world revived at a bartender
    ///     (permadeath itself is the world's own setting, chosen when the world is made)
    /// </summary>
    internal static RuleSource Hardcore => Over(Fights.WriteFor(2).Concat(Characters.WriteFor(0)).Concat([
        (EmpConfig.Server.ExportCharacter, (object)false), (EmpConfig.Server.MaxCharacters, 1),
        (EmpConfig.Server.PermadeathRevive, true),
    ]));

    /// <summary>
    ///     Big group / community (10+ players, a friend's host): the public server's chosen values (teams with their
    ///     lands and leaders, market, mail, journal, leaderboards, ownership enforced, large-crowd optimisations,
    ///     each player's own pace) without what only the public server fixes or needs: it stays a friends' game (not
    ///     public, opened by itself), the story stays shared, no host log in reports, no fixed list of display mods
    /// </summary>
    internal static RuleSource Community => Over(OfficialChosenValues()
        .Where(v => v.Entry != EmpConfig.Server.ClientOnlyMods && v.Entry != EmpConfig.Server.ShareLogForReports &&
                    v.Entry != EmpConfig.Server.TeamOwnGuilds)
        .Concat([
            (EmpConfig.Server.Teams, (object)true), (EmpConfig.Server.TeamBases, true), (EmpConfig.Server.TeamBoxes, true),
            (EmpConfig.Server.ContainerAccessLog, true), (EmpConfig.Server.AdminExtras, true),
        ]));

    /// <summary>
    ///     The values a preset writes; Custom (it writes nothing) and an unknown index read as Recommended
    /// </summary>
    internal static RuleSource PresetSource(int preset) => (preset >= 0 && preset < Presets.Length ? Presets[preset] : "") switch {
        "duo" => Duo,
        "hardcore" => Hardcore,
        "community" => Community,
        "official" => Official,
        _ => RuleSource.Defaults,
    };

    /// <summary>
    ///     Rules the official server fixes whatever its operator ticks (plan_official_server 2026-10-28, section 1):
    ///     the world cannot be taken away or hidden, every player is on the server, and the guest-side safety nets
    ///     stay on. MembersOnly is not here: stage 1 of the opening is a closed test, the preset has it off
    /// </summary>
    internal static (ConfigEntryBase Entry, object Value)[] OfficialFixedValues() => [
        (EmpConfig.Server.ImportCharacter, false), (EmpConfig.Server.ExportCharacter, false),
        (EmpConfig.Server.TeamOwnStory, true), (EmpConfig.Server.TeamKnowledge, true), (EmpConfig.Server.TeamBoxes, true),
        (EmpConfig.Server.WorldCopy, false), (EmpConfig.Server.Takeover, false), (EmpConfig.Server.AutoHost, false),
        (EmpConfig.Server.HiddenServer, false), (EmpConfig.Server.ServerAvatar, true), (EmpConfig.Server.ServerRestsWhenEmpty, true),
        (EmpConfig.Server.PublicGame, true), (EmpConfig.Server.SameGameVersion, true), (EmpConfig.Server.ContainerAccessLog, true),
        (EmpConfig.Server.AdminExtras, true), (EmpConfig.Server.Teams, true), (EmpConfig.Server.TeamBases, true),
        (EmpConfig.Server.PlayerKill, false),
        // council 2026-10-28: maps from the internet in a persistent shared world are unmoderated, with no undo
        (EmpConfig.Server.GuestMoongate, false),
        ..Fallbacks().Select(e => ((ConfigEntryBase)e, (object)true)),
    ];

    internal static HashSet<ConfigEntryBase> OfficialFixedEntries() => [..OfficialFixedValues().Select(f => f.Entry)];

    /// <summary>
    ///     The official preset: the recommended rules with the official server's values written over them (also a
    ///     "public server" preset for any host). (C) = still to be decided by the council: the plan's value for now
    /// </summary>
    internal static RuleSource Official => Over(OfficialFixedValues().Concat(OfficialChosenValues()));

    private static IEnumerable<(ConfigEntryBase Entry, object Value)> OfficialChosenValues()
    {
        // the Multiplayer menu's team and community pages (user 2026-10-29: no emblem editor on the official preset)
        yield return (EmpConfig.Server.TeamBanner, true);
        yield return (EmpConfig.Server.TeamSheets, true);
        yield return (EmpConfig.Server.ActivityJournal, true);
        yield return (EmpConfig.Server.Leaderboards, true);
        yield return (EmpConfig.Server.Market, true);
        yield return (EmpConfig.Server.Mailbox, true);
        yield return (EmpConfig.Server.TeamQuestSharing, true);
        // a guest's bug report carries the host's log too (user 2026-10-30: several causes were on the host)
        yield return (EmpConfig.Server.ShareLogForReports, true);
        // who joins: both checks; characters: pick one of the account's, 3 at most, may be deleted, revive after permadeath
        yield return (EmpConfig.Server.SourceValidationSet, "sources,plugins");
        yield return (EmpConfig.Server.ChooseCharacter, true);
        yield return (EmpConfig.Server.MaxCharacters, 3);
        yield return (EmpConfig.Server.DeleteCharacter, true);
        yield return (EmpConfig.Server.PermadeathRevive, true);
        yield return (EmpConfig.Server.KickKeepOutMinutes, 60);
        yield return (EmpConfig.Server.PublishMods, true);
        // look-only mods (pictures, portraits, sounds, translations) are each player's own (user 2026-10-30)
        yield return (EmpConfig.Server.LookModsOwn, true);
        // a display mod with code, each player's own (user 2026-10-30: Somewhat Enhanced Display, Workshop 3781674985;
        // its YK Framework is this mod's own dependency, on every side already)
        yield return (EmpConfig.Server.ClientOnlyMods, "maccha_coffee_somewhat_enhanced_display");
        // StrictValidationMode: on after stage 1 (C, council pending); it is a file-only switch, left as it is here
        yield return (EmpConfig.Server.MembersOnly, false);
        // travel and time: (C, council pending) personal pace with a 24 hour lead
        yield return (EmpConfig.Server.IndependentTravel, true);
        yield return (EmpConfig.Server.WorldMapTime, Net.WorldMapTime.PersonalPace);
        yield return (EmpConfig.Server.WorldMapLeadCap, 24);
        yield return (EmpConfig.Server.PlayerCombatTime, true);
        yield return (EmpConfig.Server.PlayerClock, true);
        yield return (EmpConfig.Server.PlayerStepPace, true);
        yield return (EmpConfig.Server.SharedAverageSpeed, false);
        yield return (EmpConfig.Server.SharedWorldTime, true);
        yield return (EmpConfig.Server.WorldKeeper, true);
        yield return (EmpConfig.Server.OwnSleep, true);
        yield return (EmpConfig.Server.NightPasses, NightRule.Half);
        yield return (EmpConfig.Server.IdleMinutes, 5);
        yield return (EmpConfig.Server.TimeStopsWhenEmpty, true);
        yield return (EmpConfig.Server.AbsentQuestsWait, true);
        yield return (EmpConfig.Server.CatchUpDays, 7);
        yield return (EmpConfig.Server.LivingWorldMinutes, 0);
        yield return (EmpConfig.Server.GuestBuild, true);
        yield return (EmpConfig.Server.HostManagesBase, false);
        yield return (EmpConfig.Server.Duels, true);
        yield return (EmpConfig.Server.GuestBouts, true);
        yield return (EmpConfig.Server.DuelArena, true);
        yield return (EmpConfig.Server.DuelBets, false);
        yield return (EmpConfig.Server.PlayerTrade, true);
        yield return (EmpConfig.Server.Follow, true);
        yield return (EmpConfig.Server.PrivateChat, true);
        yield return (EmpConfig.Server.GuestLandingPortal, true);
        // council 2026-10-28, Q4: strangers meet in the wild; the land rules, the visitor cap and the upload filter hold
        yield return (EmpConfig.Server.StrangerVisits, true);
        yield return (EmpConfig.Server.GuestSkyHelm, true);
        yield return (EmpConfig.Server.SharedMarriages, true);
        yield return (EmpConfig.Server.AffinityPerPlayer, true);
        yield return (EmpConfig.Server.TeamApproval, true);
        yield return (EmpConfig.Server.TeamDeed, true);
        yield return (EmpConfig.Server.TeamLeaders, true);
        yield return (EmpConfig.Server.TeamMaxSize, 10);
        yield return (EmpConfig.Server.TeamFoundCooldown, 30);
        yield return (EmpConfig.Server.TeamPersonal, true);
        yield return (EmpConfig.Server.LandRules, true);
        // the start meadow is everyone's, like a town (user 2026-10-30: a joining player builds there at once)
        yield return (EmpConfig.Server.CommonsStartLand, true);
        yield return (EmpConfig.Server.LandAbandon, true);
        yield return (EmpConfig.Server.AbandonDays, 30);
        yield return (EmpConfig.Server.LandsPerTeam, 2);
        // off until its check is green
        yield return (EmpConfig.Server.SiblingLands, false);
        yield return (EmpConfig.Server.SpawnZone, "start");
        yield return (EmpConfig.Server.TeamPvp, false);
        yield return (EmpConfig.Server.TeamBills, true);
        yield return (EmpConfig.Server.TeamStory, true);
        yield return (EmpConfig.Server.TeamOwnGuilds, true);
        yield return (EmpConfig.Server.AutoReconnect, true);
        yield return (EmpConfig.Server.AutoResync, true);
        yield return (EmpConfig.Server.SoftRecall, true);
        yield return (EmpConfig.Server.AutoSave, true);
        yield return (EmpConfig.Server.EnforceOwnership, true);
        yield return (EmpConfig.Server.LargeCrowds, true);
        // stage 2 of PLAN_100_players: a public server has the big world and the joins in a row
        yield return (EmpConfig.Server.FastJoinsAndSaves, true);
    }

    /// <summary>
    ///     At the start of a session this game hosts as the official server: the fixed rules are written over what the
    ///     file says (no undo entry: it is not a choice of the operator)
    /// </summary>
    internal static void ForceOfficial()
    {
        // (a peer-hosted world plays its signed rules, PeerRules, whoever keeps it)
        if (!OfficialServer.Hosting || Helper.PeerHosting.Active) {
            return;
        }

        var forcing = _forcing;
        _forcing = true;
        try {
            if (Write(OfficialFixedValues())) {
                EmpLog.Information("Official server: the fixed rules were written over the file");
            }
        } finally {
            _forcing = forcing;
        }
    }

    /// <summary>
    ///     The official server started headless (-empserver with the key and the account): the whole "Public server
    ///     (official)" preset is written at each start, the fixed rules and the chosen values, plus StrictValidationMode
    ///     (council 2026-10-28, Q3: on from stage 1). The operator never ticks anything. A file ElinMP/official_members_only
    ///     (empty is fine) keeps MembersOnly on, for the closed test of stage 1. What changed is logged
    /// </summary>
    internal static void ApplyOfficialPreset(bool membersOnly)
    {
        // (every host rule as preset 3 writes it, and every value of the official lists even when no page shows it)
        var entries = HostRules().Concat(OfficialFixedValues().Concat(OfficialChosenValues()).Select(v => v.Entry)).Distinct();
        var changes = entries.Where(e => e != EmpConfig.Server.MembersOnly).Select(e => (Entry: e, Value: Official.Raw(e)!))
            .Append((EmpConfig.Server.StrictValidationMode, (object)true))
            .Append((EmpConfig.Server.MembersOnly, (object)membersOnly))
            .Where(c => c.Value is not null).ToArray();
        var changed = changes.Where(c => !Equals(c.Entry.BoxedValue, c.Value))
            .Select(c => $"{c.Entry.Definition.Key}={c.Value}").ToArray();

        var forcing = _forcing;
        _forcing = true;
        try {
            Write(changes);
        } finally {
            _forcing = forcing;
        }

        EmpLog.Information("Official server: the official preset was applied ({Count} rules checked, {Changed} changed{List}), " +
                           "StrictValidationMode on, MembersOnly {MembersOnly}",
            changes.Length, changed.Length, changed.Length == 0 ? "" : ": " + string.Join(", ", changed), membersOnly ? "on" : "off");
    }

    private static bool _forcing;

    /// <summary>
    ///     The rows (choices and boxes not under a choice) whose value differs between two sources: what makes a
    ///     world "Custom". A box a choice leaves alone (turn-based under each player's pace) does not count
    /// </summary>
    internal static List<string> Differences(RuleSource current, RuleSource target)
    {
        var rows = Rows().Where(r => r.Under is null && r.Id != "hidden_server");
        return [
            ..rows.Where(r => r.Choice is { } c ? c.Read(current) != c.Read(target) : r.Entry is { } e && current.Get(e) != target.Get(e))
                .Select(r => r.Id),
        ];
    }

    /// <summary>
    ///     The first preset the rules match, Custom when none
    /// </summary>
    internal static int Preset(RuleSource current)
    {
        for (var i = 0; i < Presets.Length; i++) {
            if (i != CustomPreset && Differences(current, PresetSource(i)).Count == 0) {
                return i;
            }
        }

        return CustomPreset;
    }

    /// <summary>
    ///     Every host rule as the preset has it (Reset to recommended is preset 0); "Custom" writes nothing. Never a
    ///     player's own option (HostRules has none of them, the hidden server is left out), never a rule the public
    ///     server fixes while this game hosts it (Write), and the list of mods each player may have or not only from
    ///     the public server preset, which has its own: a host's list of display mods is not a way of playing
    /// </summary>
    internal static void ApplyPreset(int preset)
    {
        if (preset < 0 || preset >= Presets.Length || preset == CustomPreset) {
            // "Custom" writes nothing: redraw so the line shows the real preset again
            MarkDirty();
            return;
        }

        var target = PresetSource(preset);
        Write(HostRules().Where(e => e != EmpConfig.Server.HiddenServer && (preset == OfficialPreset || e != EmpConfig.Server.ClientOnlyMods))
            .Select(e => (e, target.Raw(e)!)));
    }

    // what the host's rules were before each change made from a page, for this session only
    private static readonly Stack<(ConfigDefinition Key, object Value)[]> _undo = new();
    private static bool _undoing;

    internal static bool CanUndo => _undo.Count > 0;

    internal static void Undo()
    {
        if (_undo.Count == 0) {
            return;
        }

        var config = EmpMod.Instance.Config;
        var before = _undo.Pop();
        _undoing = true;
        try {
            Write(before.Where(b => config.ContainsKey(b.Key)).Select(b => (config[b.Key], b.Value)));
        } finally {
            _undoing = false;
        }
    }

    /// <summary>
    ///     The host's rules as a guest is told them (NetSessionRules, no new message): what it is not told reads null
    /// </summary>
    internal static RuleSource FromRules(NetSessionRules r)
    {
        var values = new Dictionary<ConfigEntryBase, object> {
            [EmpConfig.Server.IndependentTravel] = r.AllowIndependentTravel,
            [EmpConfig.Server.WorldMapTime] = r.WorldMapTime,
            [EmpConfig.Server.PlayerCombatTime] = r.UsePlayerCombatTime,
            [EmpConfig.Server.PlayerClock] = r.UsePlayerClock,
            [EmpConfig.Server.PlayerStepPace] = r.UsePlayerStepPace,
            [EmpConfig.Server.TurnBasedCombat] = r.UseTurnBasedCombat,
            [EmpConfig.Server.SharedAverageSpeed] = r.UseSharedSpeed,
            [EmpConfig.Server.PersonalQuests] = r.UsePersonalQuests,
            [EmpConfig.Server.PlayerShipping] = r.UsePlayerShipping,
            [EmpConfig.Server.OncePerPlayer] = r.OncePerPlayer,
            [EmpConfig.Server.SharedTax] = r.UseSharedTax,
            [EmpConfig.Server.TownBoards] = r.TownBoards,
            [EmpConfig.Server.PlayersAtBase] = r.PlayersAtBase,
            [EmpConfig.Server.GlobalChat] = r.GlobalChat,
            [EmpConfig.Server.TownLife] = r.TownLife,
            [EmpConfig.Server.ShopsFromEveryPlayer] = r.ShopsFromEveryPlayer,
            [EmpConfig.Server.NearestSpawns] = r.NearestSpawns,
            [EmpConfig.Server.ActivitiesFromActor] = r.ActivitiesFromActor,
            [EmpConfig.Server.AutoCombatPerPlayer] = r.AutoCombatPerPlayer,
            [EmpConfig.Server.SummonsFollowMaster] = r.SummonsFollowMaster,
            [EmpConfig.Server.Follow] = r.AllowFollow,
            [EmpConfig.Server.PrivateChat] = r.PrivateChat,
            [EmpConfig.Server.GuestMoongate] = r.GuestMoongate,
            [EmpConfig.Server.GuestLandingPortal] = r.GuestLandingPortal,
            [EmpConfig.Server.GuestSkyHelm] = r.GuestSkyHelm,
            [EmpConfig.Server.SharedMarriages] = r.SharedMarriages,
            [EmpConfig.Server.AffinityPerPlayer] = r.AffinityPerPlayer,
            [EmpConfig.Server.Teams] = r.UseTeams,
            [EmpConfig.Server.TeamBases] = r.TeamBases,
            [EmpConfig.Server.TeamPersonal] = r.TeamPersonal,
            [EmpConfig.Server.TeamPvp] = r.TeamPvp,
            [EmpConfig.Server.TeamKnowledge] = r.TeamKnowledge,
            [EmpConfig.Server.TeamBoxes] = r.TeamBoxes,
            [EmpConfig.Server.ContainerAccessLog] = r.ContainerLog,
            [EmpConfig.Server.AdminExtras] = r.AdminExtras,
            [EmpConfig.Server.TeamLeaders] = r.TeamLeaders,
            [EmpConfig.Server.LandRules] = r.LandRules,
            [EmpConfig.Server.CommonsStartLand] = r.CommonsStartLand,
            [EmpConfig.Server.SiblingLands] = r.SiblingLands,
            [EmpConfig.Server.LargeCrowds] = r.LargeCrowds,
            [EmpConfig.Server.TeamMaxSize] = r.TeamMaxSize,
            [EmpConfig.Server.TeamBills] = r.TeamBills,
            [EmpConfig.Server.TeamStory] = r.TeamStory,
            [EmpConfig.Server.TeamOwnStory] = r.TeamOwnStory,
            [EmpConfig.Server.TeamSheets] = r.TeamSheets,
            [EmpConfig.Server.ActivityJournal] = r.ActivityJournal,
            [EmpConfig.Server.TeamBanner] = r.TeamBanner,
            [EmpConfig.Server.Leaderboards] = r.Leaderboards,
            [EmpConfig.Server.Market] = r.Market,
            [EmpConfig.Server.Mailbox] = r.Mailbox,
            [EmpConfig.Server.TeamQuestSharing] = r.TeamQuestSharing,
            [EmpConfig.Server.ShareLogForReports] = r.ShareLogForReports,
            [EmpConfig.Server.TeamOwnGuilds] = r.TeamOwnGuilds,
            [EmpConfig.Server.GuestBuild] = r.AllowGuestBuild,
            [EmpConfig.Server.HostManagesBase] = r.HostManagesBase,
            [EmpConfig.Server.Duels] = r.AllowDuels,
            [EmpConfig.Server.GuestBouts] = r.GuestBouts,
            [EmpConfig.Server.DuelArena] = r.DuelArena,
            [EmpConfig.Server.DuelBets] = r.DuelBets,
            [EmpConfig.Server.PlayerKill] = r.AllowPlayerKill,
            [EmpConfig.Server.PlayerTrade] = r.AllowPlayerTrade,
            [EmpConfig.Server.PermadeathRevive] = r.PermadeathRevive,
            [EmpConfig.Server.OwnJournal] = r.OwnJournal,
            [EmpConfig.Server.WorldCopy] = r.KeepWorldCopy,
            [EmpConfig.Server.Takeover] = r.AllowTakeover,
            [EmpConfig.Server.NightPasses] = r.NightPasses,
            [EmpConfig.Server.IdleMinutes] = r.IdleMinutes,
            [EmpConfig.Server.TimeStopsWhenEmpty] = r.TimeStopsWhenEmpty,
            [EmpConfig.Server.CatchUpDays] = r.CatchUpDays,
            [EmpConfig.Server.LivingWorldMinutes] = r.LivingWorldMinutes,
            [EmpConfig.Server.OwnSleep] = r.UseOwnSleep,
            [EmpConfig.Server.SharedWorldTime] = r.UseSharedWorldTime,
            [EmpConfig.Server.WorldKeeper] = r.UseWorldKeeper,
            [EmpConfig.Server.DumpSparesBelt] = r.DumpSparesBelt,
            [EmpConfig.Server.AutoReconnect] = r.AllowReconnect,
            [EmpConfig.Server.AutoResync] = r.AutoResync,
            [EmpConfig.Server.SoftRecall] = r.SoftRecall,
        };
        // (a host of an older version tells no list: not shown)
        if (r.ClientOnlyMods is { } clientMods) {
            values[EmpConfig.Server.ClientOnlyMods] = clientMods;
        }

        return new(e => values.TryGetValue(e, out var v) ? v : null);
    }

    /// <summary>
    ///     The visible rows a guest is told, as label and value (the card "This world's rules", read only)
    /// </summary>
    internal static List<(string Label, string Value)> RuleLines(RuleSource source) => Lines(source, false);

    /// <summary>
    ///     The visible rows, and an Advanced one (not under a choice) only when it differs from its default: a hidden
    ///     box an old world keeps off is still told (council 2026-10-25 settings trim). all: every row not under a
    ///     choice, for notices
    /// </summary>
    private static List<(string Label, string Value)> Lines(RuleSource source, bool all)
    {
        var lines = new List<(string, string)>();
        var defaults = RuleSource.Defaults;
        foreach (var row in Rows()) {
            // (a box under a choice is told through its choice: turn-based under each player's pace means nothing)
            if (row.Advanced && (row.Under is not null || (!all && !Differs(row, source, defaults)))) {
                continue;
            }

            if (row.Choice is { } choice) {
                if (choice.Entries.All(e => source.Raw(e) is null)) {
                    continue;
                }
                var read = choice.Read(source);
                lines.Add((row.Label, read < 0 ? "emp_ui_rules_custom".lang() : choice.OptionLabel(read)));
            } else if (row.Entry is { } entry && source.Get(entry) is { } on) {
                lines.Add((row.Label, (on ? "emp_ui_rules_yes" : "emp_ui_rules_no").lang()));
            }
        }
        return lines;
    }

    // (what a guest is not told reads null: not a difference)
    private static bool Differs(SettingRow row, RuleSource source, RuleSource defaults) =>
        row.Choice is { } c ? c.Entries.Any(e => source.Raw(e) is not null) && c.Read(source) != c.Read(defaults)
        : row.Entry is { } e && source.Get(e) is { } v && v != defaults.Get(e);

    /// <summary>
    ///     The last change of the host's rules a guest was told, shown on the card
    /// </summary>
    internal static string? LastNotice { get; private set; }

    // what the host changed in the last second, told as one line (five boxes of a preset arrived as five lines)
    private static readonly List<(string Label, string Was, string Value)> _pending = [];
    private static float _flushAt;

    /// <summary>
    ///     Tells the player what the host changed: one line for one row, one summary line when several rows change
    ///     within a second (FlushNotice); before is null for the first rules of a session (the snapshot of the
    ///     join). True if a line changed
    /// </summary>
    internal static bool NoticeChanges(NetSessionRules? before, NetSessionRules after)
    {
        if (before is null) {
            LastNotice = null;
            lock (_pending) {
                _pending.Clear();
            }
            return true;
        }

        // (every row: a hidden box the host turns off is told too)
        var old = Lines(FromRules(before), true).ToDictionary(l => l.Label, l => l.Value);
        var changed = false;
        foreach (var (label, value) in Lines(FromRules(after), true)) {
            if (old.TryGetValue(label, out var was) && was != value) {
                lock (_pending) {
                    if (_pending.Count == 0) {
                        _flushAt = UnityEngine.Time.unscaledTime + 1f;
                    }
                    _pending.Add((label, was, value));
                }
                changed = true;
            }
        }
        return changed;
    }

    private static void FlushNotice()
    {
        (string Label, string Was, string Value)[] changes;
        lock (_pending) {
            if (_pending.Count == 0 || UnityEngine.Time.unscaledTime < _flushAt) {
                return;
            }

            changes = [.._pending];
            _pending.Clear();
        }

        const int named = 6;
        LastNotice = changes.Length == 1 ? "emp_ui_rules_changed".Loc(changes[0].Label, changes[0].Was, changes[0].Value)
            : "emp_ui_rules_changed_many".Loc(changes.Length,
                string.Join(", ", changes.Take(named).Select(c => c.Label)) + (changes.Length > named ? " …" : ""));
        EmpPop.Information(LastNotice);
        // (the page of a friend shows the last notice)
        _dirty = true;
    }

    private static volatile bool _dirty;

    /// <summary>
    ///     Both pages are drawn again when a rule they show changes, from here or elsewhere (the file watcher
    ///     reloads on its own thread: the next frame draws)
    /// </summary>
    internal static void Watch()
    {
        EmpMod.Instance.Config.SettingChanged += (_, e) => {
            var entry = e.ChangedSetting;
            if (entry == EmpConfig.Client.FetchMods || entry == EmpConfig.Client.KeepMods || entry == EmpConfig.Client.FetchCodeMods || HostRules().Contains(entry)) {
                _dirty = true;
            }
        };
    }

    internal static void MarkDirty() => _dirty = true;

    private static float _lastRedraw = -10f;

    internal static void Update()
    {
        FlushNotice();
        TabServerConfiguration.Tick();
        // (one redraw a second at most: on a busy server the teams and the rules change many times a minute)
        if (!_dirty || UnityEngine.Time.unscaledTime - _lastRedraw < 1f) {
            return;
        }

        _dirty = false;
        _lastRedraw = UnityEngine.Time.unscaledTime;
        LayerElinTogether.Instance?.Reopen();
        ModConfigPage.Refresh();
    }

    /// <summary>
    ///     The joining checks as one string: None and All stand alone (ticked with another, "none,all" read as no
    ///     check at all, audit of the council 2026-10-23), Source and Plugin go together
    /// </summary>
    internal static string ToggleCheck(string raw, string option, bool on)
    {
        var parts = raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0 && p != option).ToList();
        if (!on) {
            return string.Join(",", parts);
        }

        if (option is "none" or "all") {
            return option;
        }

        parts.RemoveAll(p => p is "none" or "all");
        parts.Insert(0, option);
        return string.Join(",", parts);
    }

    /// <summary>
    ///     Writes what differs, nothing else, and saves the file once
    /// </summary>
    internal static bool Write(IEnumerable<(ConfigEntryBase Entry, object Value)> changes)
    {
        // (the official server's fixed rules cannot be changed from a page, a preset, a reset or an undo)
        var fixedEntries = OfficialLocked && !_forcing ? OfficialFixedEntries() : [];
        var diff = changes.Where(c => !Equals(c.Entry.BoxedValue, c.Value) && !fixedEntries.Contains(c.Entry)).ToArray();
        if (diff.Length == 0) {
            return false;
        }

        if (!_undoing && !_forcing) {
            _undo.Push([..HostRules().Select(e => (e.Definition, e.BoxedValue))]);
        }

        var config = EmpMod.Instance.Config;
        var save = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
        try {
            foreach (var (entry, value) in diff) {
                entry.BoxedValue = value;
            }
        } finally {
            config.SaveOnConfigSet = save;
        }

        if (save) {
            config.Save();
        }

        if (diff.Any(c => c.Entry == EmpConfig.Server.SourceValidationSet)) {
            NetSession.Instance.Transport?.CreateValidation();
        }

        return true;
    }
}
