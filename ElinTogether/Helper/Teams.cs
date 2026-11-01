using System.Collections.Generic;
using System.Linq;
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     Council 2026-10-25, host options "Teams" and "Team ownership of bases" (dev/PLAN_teams.md). Not Elin's
///     factions: everyone stays in the one player faction, a team is a label the host keeps in its save
///     (<see cref="ElinNetHost.TeamList" />) and tells every game (<see cref="TeamsDelta" />). Team 0, the
///     "no team": a player in it is alone, never the teammate of another player without a team (user 2026-10-30), and
///     the land it claims is its own (<see cref="LandOwnerOf" />). An old world with no team blocks nothing
/// </summary>
[HarmonyPatch]
internal static class Teams
{
    internal const int Wanderers = 0;

    // a guest's copy of the host's book
    private static Dictionary<int, int[]> _teams = [];
    private static Dictionary<int, string> _names = [];
    private static Dictionary<int, int> _zones = [];
    private static Dictionary<int, int> _owners = [];
    private static Dictionary<int, int> _requests = [];
    private static Dictionary<int, int> _invites = [];
    private static Dictionary<int, int> _zoneRules = [];
    private static Dictionary<int, int> _bills = [];
    private static Dictionary<int, int> _story = [];
    private static bool _told;

    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    private static Dictionary<int, int[]> TeamList => KeepsBook ? ElinNetHost.TeamList : _teams;
    private static Dictionary<int, string> Names => KeepsBook ? ElinNetHost.TeamNames : _names;
    private static Dictionary<int, int> Zones => KeepsBook ? ElinNetHost.ZoneTeams : _zones;
    private static Dictionary<int, int> Owners => KeepsBook ? ElinNetHost.CardOwners : _owners;
    private static Dictionary<int, int> Requests => KeepsBook ? ElinNetHost.TeamRequests : _requests;
    private static Dictionary<int, int> Invites => KeepsBook ? ElinNetHost.TeamInvites : _invites;
    private static Dictionary<int, int> ZoneRules => KeepsBook ? ElinNetHost.ZoneRules : _zoneRules;

    // the host's memory of this session, not saved: when a leader last invited, when a player last founded a team,
    // and the invitations a player turned down (a team cannot ask that player again)
    private static readonly Dictionary<int, float> InviteAt = [];
    private static readonly Dictionary<int, float> FoundAt = [];
    private static readonly HashSet<(int Team, int Target)> Refused = [];

    [ElinPreLoad]
    private static void Forget(GameIOContext context)
    {
        _teams = [];
        _names = [];
        _zones = [];
        _owners = [];
        _requests = [];
        _invites = [];
        _zoneRules = [];
        InviteAt.Clear();
        FoundAt.Clear();
        Refused.Clear();
        ElinNetHost.TeamInvites.Clear();
        _bills = [];
        _story = [];
        TeamBanner.Forget();
        _told = false;
        ElinNetHost.TeamRequests.Clear();
    }

    /// <summary>
    ///     A guest learns the host's book
    /// </summary>
    internal static void Learn(TeamsDelta delta)
    {
        var (team, asked, known) = (TeamOf(EClass.pc?.uid ?? 0), Requests.Keys.ToArray(), _told);
        _told = true;
        _teams = new(delta.TeamList);
        _names = new(delta.Names);
        _zones = new(delta.Zones);
        _owners = new(delta.Owners);
        _requests = new(delta.Requests ?? []);
        _invites = new(delta.Invites ?? []);
        _zoneRules = new(delta.ZoneRules ?? []);
        _bills = new(delta.Bills ?? []);
        _story = new(delta.Story ?? []);
        TeamQuests.Learn(delta.QuestSharers);
        TeamBanner.Learn(delta.Banners, delta.Things);
        // (not the book told on joining: nothing changed then)
        if (known) {
            Notice(team, asked);
        }
        Redraw();
    }

    /// <summary>
    ///     Says in the log what changed for the local player: its team, a player who asks to join the team it leads
    /// </summary>
    private static void Notice(int teamBefore, int[] askedBefore)
    {
        if (!EClass.core.IsGameStarted || EClass.pc is not { } pc) {
            return;
        }

        if (TeamOf(pc.uid) is var team && team != teamBefore) {
            Msg.Say(team == Wanderers ? "emp_team_now_none".lang() : "emp_team_now".Loc(NameOf(team)));
        }

        foreach (var (uid, id) in Requests) {
            if (!askedBefore.Contains(uid) && LeaderOf(id) == pc.uid) {
                Msg.Say("emp_team_asks".Loc(PlayerName(uid), NameOf(id)));
            }
        }
    }

    /// <summary>
    ///     Host box "Team leaders manage their team": the leader's choices on the Team page, the size cap and the wait
    ///     before founding again. Off: the choices of the player list, as they were
    /// </summary>
    internal static bool LeadersManage => NetSession.Instance.Rules is { UseTeams: true, TeamLeaders: true };

    /// <summary>
    ///     The team that player leads, none when it leads none or the leaders do not manage their team
    /// </summary>
    internal static int LedBy(int charaUid)
    {
        return LeadersManage && TeamOf(charaUid) is var team and not Wanderers && LeaderOf(team) == charaUid ? team : Wanderers;
    }

    /// <summary>
    ///     The team that invited that player and waits for its answer, none when there is none
    /// </summary>
    internal static int InviteOf(int charaUid) => Invites.GetValueOrDefault(charaUid);

    internal static IEnumerable<int> InvitedBy(int teamId) => Invites.Where(i => i.Value == teamId).Select(i => i.Key);

    private static bool Full(int teamId)
    {
        return LeadersManage && NetSession.Instance.Rules.TeamMaxSize is > 0 and var cap && MembersOfTeam(teamId).Count() >= cap;
    }

    /// <summary>
    ///     A line in the log of that player, wherever it plays (the host's own, or a guest told by the host)
    /// </summary>
    internal static void Say(int charaUid, string key, params object[] args)
    {
        var line = key.Loc(args);
        if (charaUid == EClass.pc.uid) {
            Msg.SayRaw(line);
        } else if (NetSession.Instance.Transport is ElinNetHost host && host.PeerOfChara(charaUid) is >= 0 and var peer) {
            host.SendDeltaTo(peer, new MsgSayDelta { Text = line, R = 1f, G = 1f, B = 1f, A = 1f });
        }
    }

    internal static bool IsOpen(int teamId) => TeamList.TryGetValue(teamId, out var team) && team[1] == 1;

    /// <summary>
    ///     The players who ask to join that team and wait for its leader
    /// </summary>
    internal static IEnumerable<int> Asking(int teamId) => Requests.Where(r => r.Value == teamId).Select(r => r.Key);

    /// <summary>
    ///     The lands that team holds
    /// </summary>
    // (the commons is no team's, whatever the book says from before the box)
    internal static IEnumerable<int> LandsOf(int teamId) =>
        Zones.Where(z => z.Value == teamId && !Commons.Is(z.Key)).Select(z => z.Key).OrderBy(uid => uid);

    internal static string ZoneName(int zoneUid) => EClass.game.spatials.Find(zoneUid)?.Name ?? zoneUid.ToString();

    /// <summary>
    ///     That player character is in this game right now (the host's own included)
    /// </summary>
    internal static bool IsOnline(int charaUid)
    {
        return NetSession.Instance.CurrentPlayers.Any(p => p.CharaUid == charaUid) || MapPlayerChara(charaUid) != null;
    }

    /// <summary>
    ///     The player character on this map (a guest's globalCharas hold only its own party, but the map has the others)
    /// </summary>
    private static Chara? MapPlayerChara(int charaUid)
    {
        return EClass._map?.charas?.Find(c => c != null && c.uid == charaUid);
    }

    /// <summary>
    ///     The character of a player uid from what this game knows: the global list, then the map, then the party
    /// </summary>
    internal static Chara? FindChara(int charaUid)
    {
        return EClass.game.cards.globalCharas.Find(charaUid) ?? MapPlayerChara(charaUid)
            ?? EClass.pc?.party?.members.Find(c => c?.uid == charaUid);
    }

    private static IEnumerable<int> MembersOf(int[] team) => team.Skip(2);

    internal static IEnumerable<int> MembersOfTeam(int teamId) => TeamList.TryGetValue(teamId, out var team) ? MembersOf(team) : [];

    /// <summary>
    ///     The player characters of a team (none for the Wanderers: they are no group)
    /// </summary>
    internal static IEnumerable<int> MembersOf(int teamId)
    {
        return TeamList.TryGetValue(teamId, out var team) ? MembersOf(team) : [];
    }

    /// <summary>
    ///     A guest's copy of the unpaid tax bills of each team (<see cref="TeamBills.Told" />)
    /// </summary>
    internal static Dictionary<int, int> ToldBills => _bills;

    /// <summary>
    ///     A guest's copy of the teams' story journal (<see cref="TeamStory.Told" />)
    /// </summary>
    internal static Dictionary<int, int> ToldStory => _story;

    /// <summary>
    ///     Every team id, the Wanderers first
    /// </summary>
    internal static IEnumerable<int> Ids() => TeamList.Keys.Prepend(Wanderers).Distinct();

    /// <summary>
    ///     The team of that player character, the Wanderers when none
    /// </summary>
    internal static int TeamOf(int charaUid)
    {
        foreach (var (id, team) in TeamList) {
            if (MembersOf(team).Contains(charaUid)) {
                return id;
            }
        }

        return Wanderers;
    }

    internal static string NameOf(int teamId)
    {
        return Names.TryGetValue(teamId, out var name) && !string.IsNullOrEmpty(name) ? name : "emp_team_wanderers".lang();
    }

    internal static int LeaderOf(int teamId)
    {
        return TeamList.TryGetValue(teamId, out var team) ? team[0] : 0;
    }

    internal static string PlayerName(int charaUid)
    {
        if (FindChara(charaUid) is { } chara) {
            return chara.NameSimple;
        }

        // (not on this map: the name the session player list carries)
        var user = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.CharaUid == charaUid)?.User.Name ??
                   NetSession.Instance.ServerRoster.Find(p => p.CharaUid == charaUid)?.Name;
        return string.IsNullOrWhiteSpace(user) ? "?" : user!;
    }

    internal static int TeamOfZone(Zone? zone)
    {
        return LandOwnerOf(zone) is > 0 and var team ? team : Wanderers;
    }

    /// <summary>
    ///     Whose land it is, counted as <see cref="StoryOwner" /> counts owners: its team, minus the claimer's chara uid
    ///     for a land claimed by a player in no team (its own, user 2026-10-30), 0 for nobody's (the commons, a land
    ///     claimed before teams or released by its team)
    /// </summary>
    internal static int LandOwnerOf(Zone? zone)
    {
        return zone is null || Commons.Is(zone) ? 0 : Zones.GetValueOrDefault(zone.uid);
    }

    /// <summary>
    ///     The player in no team whose own land it is, 0 when none
    /// </summary>
    internal static int SoloOwnerOf(Zone? zone)
    {
        return LandOwnerOf(zone) is < 0 and var owner ? -owner : 0;
    }

    /// <summary>
    ///     Whose land it is, as a name: the team's, the player's for a land of a player in no team
    /// </summary>
    internal static string LandOwnerName(Zone? zone)
    {
        return SoloOwnerOf(zone) is > 0 and var solo ? PlayerName(solo) : NameOf(TeamOfZone(zone));
    }

    /// <summary>
    ///     The owner holds a land: a team, or minus the chara uid of a player in no team (<see cref="StoryOwner" />)
    /// </summary>
    internal static bool HasBase(int teamId)
    {
        return teamId != Wanderers && LandsOf(teamId).Any();
    }

    /// <summary>
    ///     The host's spawn zone: the meadow where a new game starts, none for "the host's map", or a zone by id
    /// </summary>
    internal static Zone? SpawnZone()
    {
        return EmpConfig.Server.SpawnZone.Value switch {
            "host" => null,
            "start" or "" => EClass.game.StartZone,
            // (a zone's number, as the bench and the zone list show it, or its id: "vernis")
            var id when int.TryParse(id, out var uid) => EClass.game.spatials.Find(uid),
            var id => EClass.game.spatials.Find(id),
        };
    }

    /// <summary>
    ///     The land of that player's team with a base (the preferred one when it is the team's), none when the lands
    ///     are not owned or its team has none: the shipping sale pays the hearth of its own team, not the last base
    ///     anyone used
    /// </summary>
    internal static Zone? OwnLand(int charaUid, int preferredUid)
    {
        if (!BasesOwned) {
            return null;
        }

        // (a player in no team: its own lands)
        var uids = LandsOf(StoryOwner.Of(charaUid)).ToList();
        var uid = uids.Contains(preferredUid) ? preferredUid : uids.FirstOrDefault(u => EClass.game.spatials.Find(u)?.branch is not null);
        return uid == 0 ? null : EClass.game.spatials.Find(uid) is { branch: not null } zone ? zone : null;
    }

    internal static int OwnerOf(Card card)
    {
        return Owners.GetValueOrDefault(card.uid);
    }

    internal static bool BasesOwned => NetSession.Instance.Rules is { UseTeams: true, TeamBases: true };

    private static bool PersonalOwned => NetSession.Instance.Rules is { UseTeams: true, TeamPersonal: true };

    /// <summary>
    ///     The player other than that one who owns this card of the map, or the installed card it lies in (personal
    ///     ownership inside a team); 0 when none. What a character carries is its own business
    /// </summary>
    private static int OtherOwner(Chara chara, Card card)
    {
        if (!PersonalOwned || !chara.IsPlayer || card.GetRootCard() is not { } root || root is Chara) {
            return 0;
        }

        var owner = OwnerOf(card) is var own and not 0 ? own : OwnerOf(root);
        return owner == chara.uid ? 0 : owner;
    }

    // what a land lets a player do (host box "Land rules"): nothing, walk and talk and trade at shops only, also
    // open containers and take things, everything
    internal const int Closed = 0;
    internal const int Visit = 1;
    internal const int Use = 2;
    internal const int Build = 3;

    // a land with no rule of its own: its team builds, others visit (the game as it was)
    private const int DefaultRule = Build | Visit << 2;

    internal static bool LandRulesOn => BasesOwned && NetSession.Instance.Rules.LandRules;

    /// <summary>
    ///     A rule as one number: the level of the team (bits 0-1), of everyone else (2-3), and "closed to others while
    ///     no member is online" (bit 4)
    /// </summary>
    internal static int PackRule(int mine, int others, bool offlineClosed = false)
    {
        return mine & 3 | (others & 3) << 2 | (offlineClosed ? 1 << 4 : 0);
    }

    internal static int RuleOf(int zoneUid) => ZoneRules.GetValueOrDefault(zoneUid, DefaultRule);

    internal static int MineLevel(int rule) => rule & 3;

    internal static int OthersLevel(int rule) => rule >> 2 & 3;

    internal static bool OfflineClosed(int rule) => (rule >> 4 & 1) == 1;

    /// <summary>
    ///     Someone of that team is in the game right now
    /// </summary>
    internal static bool AnyOnline(int teamId)
    {
        return MembersOfTeam(teamId).Any(IsOnline);
    }

    /// <summary>
    ///     What that character may do on this land, as a level: everything on a land of no team (and for residents and
    ///     companions: only players are asked)
    /// </summary>
    internal static int LevelOn(Chara chara, Zone? zone)
    {
        // (a land of a player in no team is that player's alone, as a team's is its team's)
        var owner = LandOwnerOf(zone);
        if (!BasesOwned || !chara.IsPlayer || owner == 0) {
            return Build;
        }

        var own = owner == StoryOwner.Of(chara.uid);
        if (!LandRulesOn) {
            // the game as it was: a team's land is its own to build, others visit
            return own ? Build : Visit;
        }

        var rule = RuleOf(zone!.uid);
        if (own) {
            return MineLevel(rule);
        }

        // (closed to others while the team is away: the team's own level is never touched)
        return OfflineClosed(rule) && !(owner > 0 ? AnyOnline(owner) : IsOnline(-owner)) ? Closed : OthersLevel(rule);
    }

    /// <summary>
    ///     That character may do this much on that land. Only players are asked, residents and companions go on as before
    /// </summary>
    internal static bool Allowed(Chara chara, Zone? zone, int need)
    {
        return LevelOn(chara, zone) >= need;
    }

    /// <summary>
    ///     The reason (a text id) that character may not enter this land, none when it may. Only a land closed to it
    ///     refuses: a player never stays out of the world map, nor out of a land of no team
    /// </summary>
    internal static string? EntryDenied(Chara? chara, Zone? zone)
    {
        return chara is null || zone is not { IsRegion: false } || Allowed(chara, zone, Visit) ? null : "emp_land_closed";
    }

    /// <summary>
    ///     That character may use this land: a land of no team or of its team (its team's rule of the land, when
    ///     lands have rules)
    /// </summary>
    internal static bool CanTouch(Chara chara, Zone? zone)
    {
        return Allowed(chara, zone, Build);
    }

    /// <summary>
    ///     That character may take or open this card of the map (on the ground, installed, in a chest): its own card,
    ///     or the land allows it. What a character carries is not the land's
    /// </summary>
    internal static bool CanTake(Chara chara, Card card)
    {
        if (card.parent is null || OwnerOf(card) == chara.uid) {
            return true;
        }

        // a teammate's own chest, door or bed, and what lies in that chest
        if (OtherOwner(chara, card) != 0) {
            return false;
        }

        return !BasesOwned || card.GetRootCard() is Chara || Allowed(chara, EClass._zone, Use);
    }

    /// <summary>
    ///     That character may open this chest of the map: the land allows it. A teammate's own chest opens, what
    ///     is inside stays (<see cref="CanTake" />)
    /// </summary>
    internal static bool CanOpen(Chara chara, Card card)
    {
        return !BasesOwned || card.parent is null || OwnerOf(card) == chara.uid || card.GetRootCard() is Chara ||
               Allowed(chara, EClass._zone, Use);
    }

    /// <summary>
    ///     "This belongs to (team).", or "to (player)." for a card a player owns, in the log
    /// </summary>
    internal static void SayBelongs(Card? card = null)
    {
        SE.Beep();
        var owner = card is null ? 0 : OtherOwner(EClass.pc, card);
        Msg.Say("emp_team_belongs".Loc(owner != 0 ? PlayerName(owner) : LandOwnerName(EClass._zone)));
    }

    /// <summary>
    ///     A container, a door or a bed a player installs on its team's land is that player's (personal ownership):
    ///     written in the game that keeps the book, asked of it by a player who builds on a map it holds
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TaskBuild), nameof(TaskBuild.OnProgressComplete))]
    private static void OnBuilt(TaskBuild __instance)
    {
        // held: the player builds it; build mode: the invisible build agent does, for "the player" (the host, or
        // the guest whose build is replayed here, see AgentTaskDelta)
        var builder = __instance.owner switch {
            { IsPlayer: true } player when __instance.useHeld => player,
            { IsAgent: true } when !__instance.useHeld => EClass.pc,
            _ => null,
        };
        // (build mode makes the card from the recipe and keeps no target: it is the one now installed there)
        var built = __instance.target ?? __instance.pos?.Installed;
        if (!PersonalOwned || builder is null ||
            built is not Thing { IsInstalled: true } thing || thing.trait is not (TraitContainer or TraitDoor or TraitBed)) {
            return;
        }

        if (KeepsBook) {
            Own(builder, thing.uid, EClass._zone.uid);
        } else if (builder.IsPC && NetSession.Instance.IsAway) {
            // (on the host's map the host builds it and writes it itself)
            Ask(TeamAsk.Own, thing.uid, EClass._zone.uid);
        }
    }

    private static void Own(Chara chara, int cardUid, int zoneUid)
    {
        var team = TeamOf(chara.uid);
        if (team == Wanderers || Zones.GetValueOrDefault(zoneUid) != team || Owners.ContainsKey(cardUid)) {
            return;
        }

        Owners[cardUid] = chara.uid;
        EmpLog.Information("Card {CardUid} on land {ZoneUid} is player {CharaUid}'s", cardUid, zoneUid, chara.uid);
        Tell();
    }

    /// <summary>
    ///     "Share with team" in the full menu of a card the local player owns: it is the team's again
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActPlan), nameof(ActPlan._Update))]
    private static void OnUpdatePlan(ActPlan __instance)
    {
        if (!PersonalOwned || __instance.input != ActInput.AllAction) {
            return;
        }

        foreach (var card in __instance.list.Select(i => i.tc).OfType<Thing>().Where(t => t.IsInstalled && OwnerOf(t) == EClass.pc.uid)
                     .Distinct().ToArray()) {
            __instance.TrySetAct("emp_team_share", () => {
                Ask(TeamAsk.Share, card.uid);
                SE.Click();
                return false;
            }, card);
        }
    }

    /// <summary>
    ///     A door a teammate owns: the local player does not step through, the bump says whose it is
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.TryMove))]
    private static bool OnTryMove(Chara __instance, Point newPoint, ref Card.MoveResult __result)
    {
        if (!PersonalOwned || !__instance.IsPC || !newPoint.IsValid || !newPoint.cell.hasDoor) {
            return true;
        }

        foreach (var thing in newPoint.Things) {
            if (thing.trait is TraitDoor && thing.IsInstalled && OtherOwner(__instance, thing) != 0) {
                SayBelongs(thing);
                __result = Card.MoveResult.Fail;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     A chest of another team's land: no open action (nor the lock to pick), the click says whose it is
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitContainer), nameof(TraitContainer.TrySetAct))]
    private static bool OnContainerAct(TraitContainer __instance, ActPlan p)
    {
        var owner = __instance.owner;
        if (owner.parent is not Zone || CanOpen(EClass.pc, owner)) {
            return true;
        }

        p.TrySetAct("actContainer", () => {
            SayBelongs(owner);
            return false;
        }, owner, CursorSystem.Container);
        return false;
    }

    /// <summary>
    ///     The delivery chest's "Eject" of another team's land: the same click as its open action
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitDeliveryChest), nameof(TraitDeliveryChest.TrySetAct))]
    private static bool OnDeliveryChestAct(TraitDeliveryChest __instance, ActPlan p)
    {
        return OnContainerAct(__instance, p);
    }

    /// <summary>
    ///     Crafting takes from the chests and floor of the map: not what this player may not take
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Props), nameof(Props.ListThingStack))]
    private static void OnListThingStack(ThingStack __result)
    {
        if (!NetSession.Instance.Rules.UseTeams || __result.list.RemoveAll(t => !CanTake(EClass.pc, t)) == 0) {
            return;
        }

        __result.count = __result.list.Sum(t => t.Num);
        __result.max = __result.list.Select(t => t.Num).DefaultIfEmpty().Max();
    }

    /// <summary>
    ///     A recipe takes back the ingredient it remembers (or the first of its kind) from the map's stock directly,
    ///     not through the list above: not what this player may not take, another one this player may take instead
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Recipe.Ingredient), nameof(Recipe.Ingredient.RefreshThing))]
    private static void OnRefreshIngredient(Recipe.Ingredient __instance, ref Thing? __result)
    {
        if (!NetSession.Instance.Rules.UseTeams || __result is null || CanTake(EClass.pc, __result)) {
            return;
        }

        __result = EClass.pc.things.Find(__instance.id, __instance.mat, __instance.refVal) ??
                   EClass._map.Stocked.ListThingStack(__instance, StockSearchMode.AroundPC).list.FirstOrDefault();
        __instance.thing = __result;
    }

    /// <summary>
    ///     Build mode lists the map's stock as things to place: not what this player may not take (a teammate's own
    ///     chest)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Props), nameof(Props.ShouldListAsResource))]
    private static void OnListAsResource(Thing t, ref bool __result)
    {
        __result = __result && (!NetSession.Instance.Rules.UseTeams || CanTake(EClass.pc, t));
    }

    /// <summary>
    ///     Auto-dump does not fill a chest this player may not open
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TaskDump), nameof(TaskDump.IsValidContainer))]
    private static void OnDumpContainer(Thing c, ref bool __result)
    {
        __result = __result && CanOpen(EClass.pc, c);
    }

    /// <summary>
    ///     On the host (or the game holding a zone): that player may not change this land, the one the delta
    ///     changes, nor the one it stands on (a late delta of a player who just went elsewhere)
    /// </summary>
    internal static bool RefusesPeer(ElinNetHost host, int peerId, Zone? zone)
    {
        return host.ActiveRemoteCharas.TryGetValue(peerId, out var chara) && (!CanTouch(chara, zone) || !CanTouch(chara, chara.currentZone));
    }

    /// <summary>
    ///     Build mode on another team's land: not offered (the menu button, the hotbar)...
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.CanEnterBuildMode), MethodType.Getter)]
    // (...and offered in Vernis as a commons while it is the town's own: the game only opens it on the player's land)
    private static void OnCanEnterBuildMode(Zone __instance, ref bool __result)
    {
        __result = (__result || Commons.IsOpenTown(__instance)) && CanTouch(EClass.pc, __instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.CanEnterBuildModeAnywhere), MethodType.Getter)]
    private static void OnCanEnterBuildModeAnywhere(Zone __instance, ref bool __result)
    {
        __result = (__result || Commons.IsOpenTown(__instance)) && CanTouch(EClass.pc, __instance);
    }

    /// <summary>
    ///     ...and its key says whose land it is
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActionMode), nameof(ActionMode.DoFunc), typeof(CoreConfig.GameFunc))]
    private static bool OnDoFunc(CoreConfig.GameFunc func)
    {
        if (func != CoreConfig.GameFunc.ToggleBuild || EClass.scene.actionMode.IsBuildMode || CanTouch(EClass.pc, EClass._zone)) {
            return true;
        }

        SayBelongs();
        return false;
    }

    /// <summary>
    ///     The base's board of another team's land: the click says whose it is instead of opening the base's menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitHomeBoard), nameof(TraitHomeBoard.TrySetAct))]
    private static bool OnHomeBoardAct(TraitHomeBoard __instance, ActPlan p)
    {
        if (!EClass._zone.IsPCFaction || CanTouch(EClass.pc, EClass._zone)) {
            return true;
        }

        p.TrySetAct("actLayerHome", () => {
            SayBelongs();
            return false;
        }, __instance.owner);
        return false;
    }

    /// <summary>
    ///     The core of another team's land: no rename, spawn, home nor abandon, the click says whose land it is
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitCoreZone), nameof(TraitCoreZone.TrySetAct))]
    private static bool OnCoreZoneAct(TraitCoreZone __instance, ActPlan p)
    {
        if (!EClass._zone.IsPCFaction || CanTouch(EClass.pc, EClass._zone)) {
            return true;
        }

        p.TrySetAct("actNameZone", () => {
            SayBelongs();
            return false;
        }, __instance.owner);
        return false;
    }

    /// <summary>
    ///     ...and no other way to abandon it (a late act, the console)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.AbandonZone))]
    private static bool OnAbandonZone(Zone __instance)
    {
        return !EClass.pc.IsPC || !BasesOwned || __instance != EClass._zone || CanTouch(EClass.pc, __instance);
    }

    /// <summary>
    ///     What the bench and the console read: every team, its leader and members, and the teams of the lands
    /// </summary>
    internal static string Describe()
    {
        var lines = TeamList.Select(t =>
            $"{t.Key} {NameOf(t.Key)} leader {t.Value.ElementAtOrDefault(0)} open {t.Value.ElementAtOrDefault(1)} members [{string.Join(",", MembersOf(t.Value))}]");
        var zones = Zones.Select(z => $"zone {z.Key} -> team {z.Value}");
        return string.Join("\n", lines.Concat(zones).DefaultIfEmpty("no teams"));
    }

    internal const int NameMax = 24;

    /// <summary>
    ///     A team name a player typed: one line, no control characters, at most <see cref="NameMax" /> characters
    /// </summary>
    internal static string CleanName(string? text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > NameMax ? clean[..NameMax].Trim() : clean;
    }

    /// <summary>
    ///     A team id never given before: above the last one given and every id any book of this world holds (an old save
    ///     has no counter), so a disbanded team's books stay its own
    /// </summary>
    private static int NextId()
    {
        var top = new[] {
            ElinNetHost.TeamLastId, Wanderers,
            TeamList.Keys.DefaultIfEmpty().Max(), ElinNetHost.TeamSeen.Keys.DefaultIfEmpty().Max(),
            ElinNetHost.TeamDeeds.Keys.DefaultIfEmpty().Max(), ElinNetHost.TeamNames.Keys.DefaultIfEmpty().Max(),
            ElinNetHost.TeamLoreBook.Keys.DefaultIfEmpty().Max(), ElinNetHost.TeamTaxBills.Keys.DefaultIfEmpty().Max(),
            ElinNetHost.TeamStoryBook.Keys.DefaultIfEmpty().Max(), ElinNetHost.TeamStoryBundles.Keys.DefaultIfEmpty().Max(),
            ElinNetHost.TeamBoxBook.Keys.DefaultIfEmpty().Max()
        }.Max();
        return ElinNetHost.TeamLastId = top + 1;
    }

    /// <summary>
    ///     That player founds a team named so and leads it, leaving its former team. Only in the game that keeps the
    ///     book; 0 when it does not
    /// </summary>
    internal static int Found(Chara chara, string name)
    {
        if (!KeepsBook || string.IsNullOrWhiteSpace(name)) {
            return Wanderers;
        }

        var from = TeamOf(chara.uid);
        Remove(chara.uid);
        var id = NextId();
        // open: anyone joins at once; closed: its leader answers who asks (the host's rule when it is founded)
        TeamList[id] = [chara.uid, EmpConfig.Server.TeamApproval.Value ? 0 : 1, chara.uid];
        Names[id] = name.Trim();
        EmpLog.Information("Team {TeamId} {TeamName} founded by {CharaUid}", id, name, chara.uid);
        TeamKnowledge.Moved(chara, from, id);
        TeamOwnStory.Moved(chara, from, id);
        MoveLands(-chara.uid, id);
        GiveDeed(chara, id);
        Tell();
        return id;
    }

    /// <summary>
    ///     A new team's founder receives one land deed (host option), once per team and once per player: founding
    ///     again after leaving gives none
    /// </summary>
    private static void GiveDeed(Chara chara, int teamId)
    {
        if (!NetSession.Instance.Rules.UseTeams || !EmpConfig.Server.TeamDeed.Value || ElinNetHost.TeamDeeds.ContainsKey(teamId) ||
            ElinNetHost.TeamDeeds.ContainsValue(chara.uid)) {
            return;
        }

        ElinNetHost.TeamDeeds[teamId] = chara.uid;
        var deed = ThingGen.Create("deed");
        // never on the ground, where another player could take it (user 2026-10-30): the bag, else the founder's letters
        if (chara.IsPC && !chara.things.IsFull(deed)) {
            chara.Pick(deed);
        } else if (chara.IsAliveInCurrentZone && EClass._map.charas.Contains(chara) && !chara.things.IsFull(deed)) {
            // in its bag the way any change the host makes to a player's things reaches that player
            using (ElinDelta.Simulate()) {
                chara.AddThing(deed);
            }
        } else if (Mailbox.On && Mailbox.Post(chara.uid, deed, "emp_team_deed_mail".Loc(chara.NameSimple), NameOf(teamId))) {
            // (taken from the Multiplayer menu's Mail tab, into the bag)
        } else {
            var parcel = ThingGen.CreateParcel(null, deed);
            parcel.c_idRefName = "emp_team_deed_mail".Loc(chara.NameSimple);
            TeamMail.Send(chara, parcel);
        }

        EmpLog.Information("Team {TeamId}: a land deed for its founder {CharaUid}", teamId, chara.uid);
    }

    /// <summary>
    ///     That player joins the team, leaving its former one. It leads a team left without members
    /// </summary>
    internal static bool Join(Chara chara, int teamId)
    {
        if (!KeepsBook || !TeamList.ContainsKey(teamId)) {
            return false;
        }

        var from = TeamOf(chara.uid);
        if (from != teamId && Full(teamId)) {
            Requests.Remove(chara.uid);
            Say(chara.uid, "emp_team_full", NameOf(teamId));
            Tell();
            return false;
        }

        Remove(chara.uid);
        Requests.Remove(chara.uid);
        Invites.Remove(chara.uid);
        var team = TeamList[teamId];
        TeamList[teamId] = [team[0] == 0 ? chara.uid : team[0], ..team.Skip(1), chara.uid];
        EmpLog.Information("Chara {CharaUid} joined team {TeamId}", chara.uid, teamId);
        TeamKnowledge.Moved(chara, from, teamId);
        TeamOwnStory.Moved(chara, from, teamId);
        MoveLands(-chara.uid, teamId);
        Tell();
        return true;
    }

    /// <summary>
    ///     The lands of one owner become another's (<see cref="LandOwnerOf" />): a player in no team who joins or founds
    ///     a team brings its own lands to it, a returning character takes its former one's. Their rules stay. Never
    ///     past the host's "Lands per team": the lands a team has no room for stay the player's own (the oldest go
    ///     first), and the player is told
    /// </summary>
    internal static void MoveLands(int from, int to)
    {
        if (!KeepsBook || from == to || from == 0 || to == 0) {
            return;
        }

        var lands = Zones.Where(z => z.Value == from).Select(z => z.Key).OrderBy(uid => uid).ToArray();
        var kept = 0;
        if (to > 0 && BasesOwned && EmpConfig.Server.LandsPerTeam.Value is > 0 and var cap) {
            var room = System.Math.Max(0, cap - LandsOf(to).Count());
            kept = System.Math.Max(0, lands.Length - room);
            lands = lands.Take(room).ToArray();
            if (kept > 0 && from < 0) {
                Say(-from, "emp_team_land_kept", kept, cap);
                EmpLog.Information("Team {TeamId} at its cap of {Cap} lands: {Kept} lands stay player {CharaUid}'s own", to, cap, kept, -from);
            }
        }

        foreach (var land in lands) {
            Zones[land] = to;
            EmpLog.Information("Land {ZoneUid}: owner {From} -> {To}", land, from, to);
        }
    }

    /// <summary>
    ///     A character back from a solo save takes the place of the one that waited, in its team: the same seat (its
    ///     leadership too), never refused by a full team. What it brings is moved in as a join moves it
    /// </summary>
    internal static bool ReplaceMember(Chara away, Chara back)
    {
        var id = TeamOf(away.uid);
        if (!KeepsBook || id == Wanderers || away.uid == back.uid) {
            return false;
        }

        // (a back character already counted somewhere: out of it first)
        if (TeamOf(back.uid) != Wanderers) {
            Remove(back.uid);
        }

        var team = TeamList[id];
        TeamList[id] = team.Select((uid, i) => i != 1 && uid == away.uid ? back.uid : uid).ToArray();
        foreach (var book in new[] { Requests, Invites }) {
            if (book.TryGetValue(away.uid, out var value)) {
                book.Remove(away.uid);
                book[back.uid] = value;
            }
        }

        EmpLog.Information("Chara {Back} took the seat of {Away} in team {TeamId}", back.uid, away.uid, id);
        TeamKnowledge.Moved(back, Wanderers, id);
        TeamOwnStory.Moved(back, Wanderers, id);
        Tell();
        return true;
    }

    /// <summary>
    ///     That player is in no team again. Its team and the team's lands stay, even without members
    /// </summary>
    internal static bool Leave(Chara chara)
    {
        var from = TeamOf(chara.uid);
        if (!KeepsBook || !Remove(chara.uid)) {
            return false;
        }

        Requests.Remove(chara.uid);
        TeamKnowledge.Moved(chara, from, Wanderers);
        TeamOwnStory.Moved(chara, from, Wanderers);

        EmpLog.Information("Chara {CharaUid} left its team", chara.uid);
        Tell();
        return true;
    }

    /// <summary>
    ///     The local player's choice in a team menu: answered here in the game that keeps the book, asked of the
    ///     host otherwise (wherever this player is)
    /// </summary>
    internal static void Ask(TeamAsk kind, int id = 0, int zoneUid = 0, string text = "")
    {
        if (KeepsBook) {
            Answer(EClass.pc, kind, id, zoneUid, text);
        } else {
            PersonalQuests.TellHost(new TeamRequestDelta { Kind = kind, Id = id, ZoneUid = zoneUid, Text = text });
        }
    }

    /// <summary>
    ///     In the game that keeps the book: what that player asks, read against the book as it is now
    /// </summary>
    internal static void Answer(Chara chara, TeamAsk kind, int id, int zoneUid, string text = "")
    {
        if (!KeepsBook || !NetSession.Instance.Rules.UseTeams) {
            return;
        }

        var (team, asked) = (TeamOf(EClass.pc.uid), Requests.Keys.ToArray());
        switch (kind) {
            case TeamAsk.Found when MayFound(chara):
                Found(chara, CleanName(text) is { Length: > 0 } name ? name : chara.NameSimple);
                FoundAt[chara.uid] = UnityEngine.Time.realtimeSinceStartup;
                break;
            case TeamAsk.Join when TeamList.TryGetValue(id, out var wanted) && TeamOf(chara.uid) != id:
                // at once into an open team, a team without a leader, or when the host does not ask the leaders
                if (wanted[1] == 1 || wanted[0] == 0 || !EmpConfig.Server.TeamApproval.Value) {
                    Join(chara, id);
                } else {
                    Requests[chara.uid] = id;
                    Tell();
                }
                break;
            case TeamAsk.Leave:
                Leave(chara);
                break;
            case TeamAsk.Rename when LedBy(chara.uid) is var renamed and not Wanderers && CleanName(text) is { Length: > 0 } newName:
                Names[renamed] = newName;
                EmpLog.Information("Team {TeamId} renamed {TeamName} by its leader", renamed, newName);
                Tell();
                break;
            case TeamAsk.Kick when LedBy(chara.uid) is var kicker and not Wanderers && id != chara.uid && TeamOf(id) == kicker:
                Put(id, "emp_team_kicked", NameOf(kicker));
                break;
            case TeamAsk.Promote when LedBy(chara.uid) is var promoter and not Wanderers && id != chara.uid && TeamOf(id) == promoter:
                TeamList[promoter][0] = id;
                EmpLog.Information("Team {TeamId} handed to {CharaUid}", promoter, id);
                Say(id, "emp_team_promoted", NameOf(promoter));
                Tell();
                break;
            case TeamAsk.Disband when LedBy(chara.uid) is var ended and not Wanderers:
                Disband(ended);
                break;
            case TeamAsk.Invite when LedBy(chara.uid) is var inviter and not Wanderers:
                Invite(chara, inviter, id);
                break;
            // (the leader of the land's team, or the player in no team whose own land it is)
            case TeamAsk.LandRule when LandRulesOn && Zones.TryGetValue(zoneUid, out var landTeam) &&
                                       (landTeam > 0 ? LeaderOf(landTeam) == chara.uid : landTeam == -chara.uid) &&
                                       id is >= 0 and < 32 && MineLevel(id) != Closed:
                if (id == DefaultRule) {
                    ZoneRules.Remove(zoneUid);
                } else {
                    ZoneRules[zoneUid] = id;
                }

                EmpLog.Information("Land {ZoneUid} of team {TeamId}: rule {Rule}", zoneUid, landTeam, id);
                Tell();
                break;
            case TeamAsk.SetOpen when LedBy(chara.uid) is var opener and not Wanderers && id is 0 or 1:
                TeamList[opener][1] = id;
                EmpLog.Information("Team {TeamId} joining is now {Open}", opener, id == 1 ? "open" : "closed");
                Tell();
                break;
            case TeamAsk.Banner when TeamBanner.Set(chara, id, zoneUid, text):
                Tell();
                break;
            case TeamAsk.AcceptInvite when LeadersManage && Invites.TryGetValue(chara.uid, out var offered) && offered == id:
                Invites.Remove(chara.uid);
                Join(chara, id);
                break;
            case TeamAsk.RefuseInvite when LeadersManage && Invites.TryGetValue(chara.uid, out var turned) && turned == id:
                Invites.Remove(chara.uid);
                Refused.Add((id, chara.uid));
                Say(LeaderOf(id), "emp_team_invite_refused", PlayerName(chara.uid));
                Tell();
                break;
            case TeamAsk.ShareQuests when id is 0 or 1 && TeamQuests.SetSharing(chara, id == 1):
                Tell();
                break;
            case TeamAsk.Own when PersonalOwned:
                Own(chara, id, zoneUid);
                break;
            case TeamAsk.Share when Owners.TryGetValue(id, out var owner) && owner == chara.uid:
                Owners.Remove(id);
                EmpLog.Information("Card {CardUid} is shared with the team again", id);
                Tell();
                break;
            // only the leader of the team asked answers
            case TeamAsk.Accept or TeamAsk.Refuse when Requests.TryGetValue(id, out var to) && LeaderOf(to) == chara.uid:
                Requests.Remove(id);
                if (kind == TeamAsk.Accept && EClass.game.cards.globalCharas.Find(id) is { } joiner) {
                    Join(joiner, to);
                } else {
                    Tell();
                }
                break;
        }

        Notice(team, asked);
    }

    /// <summary>
    ///     The team entries of a player's row in the list of players: on one's own row found, leave, join any
    ///     other team and the leader's answers; on another player's row, join its team
    /// </summary>
    internal static IEnumerable<(string Label, System.Action Act)> MenuFor(Chara chara)
    {
        if (!NetSession.Instance.Rules.UseTeams) {
            yield break;
        }

        var mine = TeamOf(EClass.pc.uid);
        if (!chara.IsPC) {
            if (TeamOf(chara.uid) is var theirs and not Wanderers && theirs != mine) {
                yield return ("emp_team_join".Loc(NameOf(theirs)), () => Ask(TeamAsk.Join, theirs));
            }
            yield break;
        }

        if (mine == Wanderers) {
            yield return ("emp_team_found".lang(), () => Ask(TeamAsk.Found));
        } else {
            yield return ("emp_team_leave".Loc(NameOf(mine)), () => Ask(TeamAsk.Leave));
        }

        foreach (var id in TeamList.Keys.Where(id => id != mine).ToArray()) {
            yield return ("emp_team_join".Loc(NameOf(id)), () => Ask(TeamAsk.Join, id));
        }

        if (mine != Wanderers && LeaderOf(mine) == EClass.pc.uid) {
            foreach (var uid in Requests.Where(r => r.Value == mine).Select(r => r.Key).ToArray()) {
                yield return ("emp_team_accept".Loc(PlayerName(uid)), () => Ask(TeamAsk.Accept, uid));
                yield return ("emp_team_refuse".Loc(PlayerName(uid)), () => Ask(TeamAsk.Refuse, uid));
            }
        }
    }

    /// <summary>
    ///     The player a claim run on the host is for: a guest's yes to a deed (<see cref="ClaimZoneDelta" />)
    /// </summary>
    internal static Chara? Claimer { get; set; }

    /// <summary>
    ///     A land was claimed in the game that keeps the book: it is the claimer's team's, or the claimer's own when it
    ///     is in no team
    /// </summary>
    internal static void OnClaimed(Zone zone)
    {
        var claimer = Claimer ?? EClass.pc;
        // whose land it is, for what it earns (BaseIncome), teams or not
        if (KeepsBook) {
            zone.SetInt(BaseIncome.ClaimerKey, claimer.uid);
        }

        // teams off: a claim is nobody's, as before (when the box is ticked later, a claimer in no team gets it, see
        // AdoptSoloLands)
        if (!KeepsBook || !NetSession.Instance.Rules.UseTeams) {
            return;
        }

        var team = TeamOf(claimer.uid);
        // (a new claim starts with the default rule)
        ZoneRules.Remove(zone.uid);
        // (the commons is everyone's: no team takes it)
        if (Commons.Is(zone)) {
            Zones.Remove(zone.uid);
        } else {
            Zones[zone.uid] = StoryOwner.Of(team, claimer.uid);
        }

        zone.SetInt(SoloCheckedKey, 1);

        EmpLog.Information("Land {ZoneUid} claimed by {CharaUid}, team {TeamId}", zone.uid, claimer.uid, team);
        Tell();
    }

    /// <summary>
    ///     Founding is not asked again at once (host box "Team leaders manage their team")
    /// </summary>
    private static bool MayFound(Chara chara)
    {
        var wait = LeadersManage ? EmpConfig.Server.TeamFoundCooldown.Value * 60f : 0f;
        if (wait <= 0f || !FoundAt.TryGetValue(chara.uid, out var at) || UnityEngine.Time.realtimeSinceStartup - at >= wait) {
            return true;
        }

        Say(chara.uid, "emp_team_found_wait", (int)System.Math.Ceiling((wait - (UnityEngine.Time.realtimeSinceStartup - at)) / 60f));
        return false;
    }

    internal static bool AbandonOn => BasesOwned && EmpConfig.Server.LandAbandon.Value;

    private static int Today() => (int)(System.DateTime.UtcNow - new System.DateTime(2020, 1, 1)).TotalDays;

    /// <summary>
    ///     Host box "Abandoned land", once a minute: a team with someone in the game is seen today; a team nobody of which
    ///     was seen for the set days loses its claim on its lands (they are nobody's: the mod's claim, the game's own
    ///     base stays as it is). A team never seen before starts counting now
    /// </summary>
    internal static void CheckAbandon()
    {
        if (!KeepsBook || !NetSession.Instance.Rules.UseTeams) {
            return;
        }

        AdoptSoloLands();
        var (today, days, seen) = (Today(), System.Math.Max(1, EmpConfig.Server.AbandonDays.Value), ElinNetHost.TeamSeen);
        foreach (var id in TeamList.Keys.ToArray()) {
            if (AnyOnline(id) || !seen.ContainsKey(id)) {
                seen[id] = today;
            } else if (AbandonOn && today - seen[id] >= days && LandsOf(id).ToArray() is { Length: > 0 } lands) {
                foreach (var land in lands) {
                    Release(land);
                }

                EmpLog.Information("Team {TeamId} {TeamName} was away for {Days} days: its {Count} lands are nobody's", id, NameOf(id), today - seen[id],
                    lands.Length);
                Tell();
            }
        }
    }

    // a land of the player faction whose owner was settled (claimed with teams on, adopted once, or released by its
    // team): AdoptSoloLands leaves it as it is
    internal const int SoloCheckedKey = 47110002;

    /// <summary>
    ///     A land is nobody's again (its team disbanded, or away too long): it stays so, never handed to its claimer
    /// </summary>
    private static void Release(int zoneUid)
    {
        Zones.Remove(zoneUid);
        ZoneRules.Remove(zoneUid);
        EClass.game.spatials.Find(zoneUid)?.SetInt(SoloCheckedKey, 1);
    }

    /// <summary>
    ///     A claim that did not happen after all (a land made for a player away whose game never claimed it): no owner,
    ///     no claimer, as before it
    /// </summary>
    internal static void Unclaim(Zone zone)
    {
        if (!KeepsBook) {
            return;
        }

        Zones.Remove(zone.uid);
        ZoneRules.Remove(zone.uid);
        zone.SetInt(BaseIncome.ClaimerKey, 0);
        zone.SetInt(SoloCheckedKey, 0);
        Tell();
    }

    /// <summary>
    ///     User 2026-10-30, "a solo player is not in the same team as the other solo players": a land of no team that
    ///     its claimer (BaseIncome.ClaimerKey) holds while in no team, from before this rule (an old world, or claimed
    ///     while teams were off), becomes that player's own, once. A claimer now in a team, a land of no known claimer,
    ///     the commons and a land a team released stay nobody's
    /// </summary>
    private static void AdoptSoloLands()
    {
        var changed = false;
        HashSet<int>? players = null;
        foreach (var zone in EClass.pc.faction.GetChildren().Select(b => b.owner).ToArray()) {
            if (zone is null || zone.GetInt(SoloCheckedKey) != 0 || Commons.Is(zone)) {
                continue;
            }

            if (Zones.ContainsKey(zone.uid) || zone.GetInt(BaseIncome.ClaimerKey) is not (> 0 and var claimer) || TeamOf(claimer) != Wanderers) {
                zone.SetInt(SoloCheckedKey, 1);
                continue;
            }

            // any character of a player's roster, not only the one it played last; a claimer not among them waits
            // (asked again next time) unless it is gone from the world
            players ??= ElinNetHost.RosterCharaUids();
            if (!players.Contains(claimer)) {
                if (EClass.game.cards.globalCharas.Find(claimer) is null) {
                    zone.SetInt(SoloCheckedKey, 1);
                }

                continue;
            }

            zone.SetInt(SoloCheckedKey, 1);
            Zones[zone.uid] = -claimer;
            changed = true;
            EmpLog.Information("Land {ZoneUid} of no team is its claimer {CharaUid}'s own", zone.uid, claimer);
        }

        if (changed) {
            Tell();
        }
    }

    /// <summary>
    ///     That player's team (or the player itself, in no team) already holds as many lands as the host allows (box
    ///     "Lands per team"): a deed is not read
    /// </summary>
    internal static bool LandCapReached(Chara chara)
    {
        return BasesOwned && EmpConfig.Server.LandsPerTeam.Value is > 0 and var cap && LandsOf(StoryOwner.Of(chara.uid)).Count() >= cap;
    }

    /// <summary>
    ///     The deed of a team at its cap of lands is not read (the host's own player: a guest's deed is refused by the
    ///     host, see ClaimZoneDelta)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitDeed), nameof(TraitDeed.OnRead))]
    private static bool OnReadDeed(Chara c)
    {
        if (!KeepsBook || !c.IsPC || !LandCapReached(c)) {
            return true;
        }

        Msg.Say("emp_team_land_cap".Loc(EmpConfig.Server.LandsPerTeam.Value));
        return false;
    }

    /// <summary>
    ///     The leader puts that member out (it is a Wanderer, told so)
    /// </summary>
    private static void Put(int charaUid, string key, string teamName)
    {
        if (EClass.game.cards.globalCharas.Find(charaUid) is { } chara) {
            Leave(chara);
        } else if (Remove(charaUid)) {
            Tell();
        }

        Say(charaUid, key, teamName);
    }

    /// <summary>
    ///     The team ends: every member a Wanderer, its lands nobody's, its requests and invitations void
    /// </summary>
    private static void Disband(int teamId)
    {
        var name = NameOf(teamId);
        foreach (var uid in MembersOfTeam(teamId).ToArray()) {
            Put(uid, "emp_team_disbanded", name);
        }

        TeamList.Remove(teamId);
        Names.Remove(teamId);
        TeamBanner.Remove(teamId);
        foreach (var zone in Zones.Where(z => z.Value == teamId).Select(z => z.Key).ToArray()) {
            Release(zone);
        }
        foreach (var uid in Requests.Where(r => r.Value == teamId).Select(r => r.Key).ToArray()) {
            Requests.Remove(uid);
        }
        foreach (var uid in Invites.Where(r => r.Value == teamId).Select(r => r.Key).ToArray()) {
            Invites.Remove(uid);
        }

        EmpLog.Information("Team {TeamId} {TeamName} disbanded by its leader", teamId, name);
        Tell();
    }

    /// <summary>
    ///     The leader invites a player in the game: one pending invitation per player, one invitation a second per
    ///     leader, none to a player whose refusal this session is known, none to a full team
    /// </summary>
    private static void Invite(Chara leader, int teamId, int targetUid)
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (InviteAt.TryGetValue(leader.uid, out var at) && now - at < 1f) {
            return;
        }

        InviteAt[leader.uid] = now;
        if (TeamOf(targetUid) == teamId || !IsOnline(targetUid) || Invites.ContainsKey(targetUid) || Refused.Contains((teamId, targetUid))) {
            return;
        }

        if (Full(teamId)) {
            Say(leader.uid, "emp_team_full", NameOf(teamId));
            return;
        }

        Invites[targetUid] = teamId;
        Say(targetUid, "emp_team_invited", NameOf(teamId), PlayerName(leader.uid));
        Tell();
    }

    private static bool Remove(int charaUid)
    {
        var id = TeamOf(charaUid);
        if (id == Wanderers) {
            return false;
        }

        var team = TeamList[id];
        var members = MembersOf(team).Where(uid => uid != charaUid).ToArray();
        // the leader who leaves hands the team to the next member
        var leader = team[0] == charaUid ? members.FirstOrDefault() : team[0];
        TeamList[id] = [leader, team[1], ..members];
        return true;
    }

    private static void Tell()
    {
        (NetSession.Instance.Transport as ElinNetHost)?.TellTeams();
        Redraw();
    }

    /// <summary>
    ///     The team page, when it is open, shows the book as it is now
    /// </summary>
    private static void Redraw()
    {
        // (marked, not redrawn now: many team changes in one frame, as players are sorted into teams, redraw once)
        if (LayerElinTogether.Instance != null) {
            SettingsTable.MarkDirty();
        }
    }
}
