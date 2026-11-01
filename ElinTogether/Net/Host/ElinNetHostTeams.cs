using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

/// <summary>
///     Council 2026-10-25 (<see cref="Teams" />): the teams, the team of each claimed land and the owner of a card, kept
///     in the host's save. An old save has none: everyone is a Wanderer (team 0), nothing is anyone's
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Team id -> leader chara uid, open (1) or not (0), then the member chara uids. Team 0 is never here
    /// </summary>
    [ElinGameIOProperty("teams")]
    internal static Dictionary<int, int[]> TeamList
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Team id -> its name (kept apart: the save only holds plain values)
    /// </summary>
    [ElinGameIOProperty("team_names")]
    internal static Dictionary<int, string> TeamNames
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Host box "Team banners" (<see cref="TeamBanner" />): team id -> [emblem, colour]. An older save has none
    /// </summary>
    [ElinGameIOProperty("team_banners")]
    internal static Dictionary<int, int[]> TeamBanners
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Team id -> the source id of the thing chosen as its emblem. An older save has none: its element emblem draws
    /// </summary>
    [ElinGameIOProperty("team_things")]
    internal static Dictionary<int, string> TeamThings
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Zone uid -> the team of the player who claimed it, none for a land claimed by a Wanderer
    /// </summary>
    [ElinGameIOProperty("zone_team")]
    internal static Dictionary<int, int> ZoneTeams
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Zone uid -> the rule its team set for it (<see cref="Teams.PackRule" />). A land without a row has the default
    ///     rule: its team builds, others visit
    /// </summary>
    [ElinGameIOProperty("zone_rules")]
    internal static Dictionary<int, int> ZoneRules
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Team id -> the last day (counted from 2020-01-01, real time) someone of that team was in the game: the host
    ///     box "Abandoned land" reads it (<see cref="Teams.CheckAbandon" />)
    /// </summary>
    [ElinGameIOProperty("team_seen")]
    internal static Dictionary<int, int> TeamSeen
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Card uid -> the chara uid of the player it belongs to
    /// </summary>
    [ElinGameIOProperty("card_owner")]
    internal static Dictionary<int, int> CardOwners
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     [the last team id ever given]: a disbanded team's id is never reused, or a new team would inherit its bank,
    ///     knowledge, story, bills and deed. 0 in an old save: <see cref="Helper.Teams.NextId" /> starts it above every id any book holds
    /// </summary>
    [ElinGameIOProperty("team_last_id")]
    internal static int TeamLastId
    {
        get;
        set;
    }

    /// <summary>
    ///     Team id -> the founder who received its free land deed: never twice for a team, nor for a player who
    ///     founds again
    /// </summary>
    [ElinGameIOProperty("team_deeds")]
    internal static Dictionary<int, int> TeamDeeds
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Host option "Teams learn on their own" (<see cref="TeamKnowledge" />): team id -> what that team knows, as
    ///     text (<see cref="TeamLore" />). The Wanderers (team 0) too. Empty while the box was never on
    /// </summary>
    [ElinGameIOProperty("team_lore")]
    internal static Dictionary<int, string> TeamLoreBook
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     [the ledger's schema version, 1 once every team was given a copy of the world's knowledge]
    /// </summary>
    [ElinGameIOProperty("team_lore_state")]
    internal static int[] TeamLoreState
    {
        get => field is { Length: 2 } ? field : field = new int[2];
        set;
    }

    /// <summary>
    ///     Host option "Teams keep their own bank and delivery box" (<see cref="TeamBoxes" />): owner
    ///     (<see cref="StoryOwner" />) -> its two boxes, bank then delivery box, each as the text of
    ///     <see cref="TeamBoxes.Pack" />. Empty while the box was never on
    /// </summary>
    [ElinGameIOProperty("team_boxes")]
    internal static Dictionary<int, string[]> TeamBoxBook
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     [the ledger's schema version, 1 once the box has been on in this world]
    /// </summary>
    [ElinGameIOProperty("team_boxes_state")]
    internal static int[] TeamBoxState
    {
        get => field is { Length: 2 } ? field : field = new int[2];
        set;
    }

    /// <summary>
    ///     Host option "Teams pay their own land" (<see cref="TeamBills" />): team id -> its unpaid tax bills. The
    ///     world's count (<c>player.taxBills</c>) stays the sum the game knows; what it holds above these is older than
    ///     the box (bills of no team)
    /// </summary>
    [ElinGameIOProperty("team_tax_bills")]
    internal static Dictionary<int, int> TeamTaxBills
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Host option "Teams keep their own story journal" (<see cref="TeamStory" />): team id -> the furthest phase
    ///     of the main quest one of its players took part in
    /// </summary>
    [ElinGameIOProperty("team_story")]
    internal static Dictionary<int, int> TeamStoryBook
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Host option "Teams live their own story" (<see cref="TeamOwnStory" />): team id -> where that team stands
    ///     in the story, as text (<see cref="StoryBundle" />). The Wanderers (team 0) too. Empty while the box was never on
    /// </summary>
    [ElinGameIOProperty("team_story_bundle")]
    internal static Dictionary<int, string> TeamStoryBundles
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     [the ledger's schema version, 1 once every team was given the world's story, the world team (whose story
    ///     the save keeps), 1 while Ash and Fiama wait for every team before they leave for Lothria, 1 while the
    ///     stories hold each owner's guilds]
    /// </summary>
    [ElinGameIOProperty("team_story_bundle_state")]
    internal static int[] TeamStoryBundleState
    {
        // (a save of four: written before the guilds)
        get => field is { Length: 5 } ? field : field = field is { Length: 4 } ? [..field, 0] : new int[5];
        set;
    }

    /// <summary>
    ///     Chara uid -> the story steps ("quest id:phase") whose rewards that player had: once per player, whatever
    ///     team it plays the step with (team hopping farms nothing)
    /// </summary>
    [ElinGameIOProperty("team_story_had")]
    internal static Dictionary<int, string[]> TeamStoryHad
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Each connected player's peer and the chara uid it plays (away ones too)
    /// </summary>
    internal IEnumerable<(int Peer, int CharaUid)> PeerCharas()
    {
        foreach (var peer in Socket.Peers.ToArray()) {
            if (SavedRemoteCharas.TryGetValue(peer.User, out var uid)) {
                yield return (peer.Id, uid);
            }
        }
    }

    /// <summary>
    ///     Each connected peer with the chara it plays here (a zone session: its visitors)
    /// </summary>
    internal IEnumerable<(int Peer, Chara Chara)> PeerPlayers()
    {
        foreach (var peer in Socket.Peers.ToArray()) {
            if (PlayerChara(peer.Id) is { } chara) {
                yield return (peer.Id, chara);
            }
        }
    }

    /// <summary>
    ///     The chara uid of every player of this world, the host's own included
    /// </summary>
    internal static IEnumerable<int> PlayerCharaUids()
    {
        // (a server's stand-in is no player, see ServerAvatar)
        return SavedRemoteCharas.Values.Append(player.uidChara).Where(uid => !ServerAvatar.IsUid(uid)).Distinct();
    }

    /// <summary>
    ///     Every player character of this world: the last played of each account and the others of its roster
    /// </summary>
    internal static HashSet<int> RosterCharaUids()
    {
        return PlayerCharaUids().Concat(PlayerRosters.Values.SelectMany(roster => roster)).Where(uid => !ServerAvatar.IsUid(uid)).ToHashSet();
    }

    /// <summary>
    ///     The fame of that player: the host's own, another's as its game last told it
    /// </summary>
    internal static int FameOfPlayer(int charaUid)
    {
        return charaUid == player.uidChara ? player.fame
            : PlayerStandings.TryGetValue(charaUid, out var standing) && standing.Length >= 1 ? standing[StandingFame] : 0;
    }

    /// <summary>
    ///     Karma the date costs the connected players of one team (its unpaid tax, <see cref="TeamBills" />), as
    ///     <see cref="GiveKarmaToEveryone" /> does for all. The host's own player is the caller's business
    /// </summary>
    internal void GiveKarmaToTeam(int team, int karma)
    {
        if (IsZoneSession) {
            return;
        }

        foreach (var peer in Socket.Peers.ToArray()) {
            // (the owner as StoryOwner counts it: a team's players, or the one player in no team)
            if (!SavedRemoteCharas.TryGetValue(peer.User, out var uid) || StoryOwner.Of(uid) != team) {
                continue;
            }

            if (ActiveRemoteCharas.TryGetValue(peer.Id, out var chara)) {
                GiveKarma(chara, karma);
            } else {
                SendDeltaTo(peer.Id, new PlayerStandingDelta {
                    Fame = 0,
                    Karma = karma,
                    Relative = true,
                });
            }
        }
    }

    // the players sent to the spawn zone in this hosting: a reconnect finds them where they were
    private readonly HashSet<ulong> _spawned = [];

    /// <summary>
    ///     A player came in (its world and the host's map are on the way): a new player, or one whose team has no
    ///     base, travels to the host's spawn zone, the way a player travels alone. Nothing when teams are off
    /// </summary>
    internal void SendToSpawnZone(Chara chara, ISteamNetPeer peer, bool isNew)
    {
        var rules = NetSession.Instance.Rules;
        if (IsZoneSession || !rules.UseTeams || !rules.AllowIndependentTravel || (!isNew && Teams.HasBase(StoryOwner.Of(chara.uid))) ||
            Teams.SpawnZone() is not { } zone || zone == _zone || !_spawned.Add(peer.User)) {
            return;
        }

        EmpLog.Information("Player {@Peer} starts at the spawn zone {ZoneFullName}", peer, zone.ZoneFullName);
        SendDeltaTo(peer.Id, new TeamSpawnDelta { ZoneUid = zone.uid });
    }

    /// <summary>
    ///     Chara uid -> the team it asks to join, until its leader answers. Not saved: a request is asked again
    /// </summary>
    internal static Dictionary<int, int> TeamRequests { get; } = [];

    /// <summary>
    ///     Chara uid -> the team that invited it, until it answers. Not saved
    /// </summary>
    internal static Dictionary<int, int> TeamInvites { get; } = [];

    // closed lands: a player standing on a land that closed on it (the rule changed, it left or was put out of the team,
    // it arrived some way the entry checks do not see) has this long to leave, then is moved out
    private const float LandGraceSeconds = 30f;
    private readonly Dictionary<int, float> _landGrace = [];
    private readonly Dictionary<int, int> _lastOpenZone = [];
    private float _landSweepAt;
    private float _abandonAt;

    /// <summary>
    ///     Once a second, on the game that keeps the book: each player on a land closed to it is told, and after the
    ///     grace moved out by the normal way a player leaves (the game travels, a zone it holds is handed back first)
    /// </summary>
    private void UpdateLandRules()
    {
        // (abandoned lands, once a minute: days are the unit)
        if (!IsZoneSession && _zone is not null && UnityEngine.Time.unscaledTime >= _abandonAt) {
            _abandonAt = UnityEngine.Time.unscaledTime + 60f;
            Teams.CheckAbandon();
        }

        if (IsZoneSession || !Teams.LandRulesOn || _zone is null) {
            _landGrace.Clear();
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _landSweepAt) {
            return;
        }

        _landSweepAt = now + 1f;
        SweepLand(pc, pc.currentZone, -1, now);
        foreach (var peer in Socket.Peers.ToArray()) {
            // (on the host's map, or alone on the land it holds)
            var where = IsAway(peer)
                ? _leases.TryGetValue(peer.Id, out var held) ? held.Keys.Select(uid => game.spatials.Find(uid) as Zone).FirstOrDefault(z => z is { IsRegion: false }) : null
                : _settled.Contains(peer.Id) ? _zone : null;
            if (PlayerChara(peer.Id) is { } chara) {
                SweepLand(chara, where, peer.Id, now);
            }
        }
    }

    private void SweepLand(Chara chara, Zone? where, int peerId, float now)
    {
        if (where is null || Teams.EntryDenied(chara, where) is null) {
            _landGrace.Remove(chara.uid);
            if (where is not null) {
                _lastOpenZone[chara.uid] = where.uid;
            }

            return;
        }

        if (!_landGrace.TryGetValue(chara.uid, out var until)) {
            _landGrace[chara.uid] = now + LandGraceSeconds;
            Teams.Say(chara.uid, "emp_land_leave_notice", where.Name, (int)LandGraceSeconds);
            return;
        }

        if (now < until) {
            return;
        }

        _landGrace.Remove(chara.uid);
        // the last land it was welcome on, the spawn zone, the host's map, the start meadow, the world map
        var target = new[] { game.spatials.Find(_lastOpenZone.GetValueOrDefault(chara.uid)) as Zone, Teams.SpawnZone(), _zone, game.StartZone, EClass.world.region }
            .FirstOrDefault(z => z is not null && z != where && Teams.EntryDenied(chara, z) is null);
        if (target is null) {
            return;
        }

        EmpLog.Information("Player {CharaUid} is moved out of {ZoneFullName} to {Target}: the land is closed to it", chara.uid, where.ZoneFullName,
            target.ZoneFullName);
        if (peerId < 0) {
            chara.MoveZone(target, ZoneTransition.EnterState.Auto);
        } else {
            SendDeltaTo(peerId, new TeamEvictDelta { ZoneUid = where.uid, Target = target.uid });
        }
    }

    /// <summary>
    ///     The teams changed, or a player joins: everyone is told the whole book
    /// </summary>
    internal void TellTeams()
    {
        // (a zone hosted by a client keeps no books, the host of the world does)
        if (IsZoneSession) {
            return;
        }

        Delta.AddRemote(new TeamsDelta {
            TeamList = new(TeamList),
            Names = new(TeamNames),
            Zones = new(ZoneTeams),
            Owners = new(CardOwners),
            Requests = new(TeamRequests),
            Invites = new(TeamInvites),
            ZoneRules = new(ZoneRules),
            Bills = TeamBills.Told(),
            Story = TeamStory.Told(),
            Banners = TeamBanner.Told(),
            Things = TeamBanner.ToldThings(),
            QuestSharers = TeamQuests.Told(),
        });
    }
}

internal partial class ElinNetHost
{
    /// <summary>
    ///     Host box "Leaderboards" (Helper/Leaderboards.cs): player chara uid -> [deepest floor reached, duels won],
    ///     noted by the host. An older save has none
    /// </summary>
    [ElinGameIOProperty("leader_stats")]
    internal static Dictionary<int, int[]> LeaderStats
    {
        get => field ??= [];
        set;
    }
}
