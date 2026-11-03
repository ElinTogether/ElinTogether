using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    public readonly RemoteCharaMap ActiveRemoteCharas = [];

    /// <summary>
    ///     When each player's link dropped without anyone leaving (see <see cref="Common.EmpDisconnectInfo.IsLinkLost" />):
    ///     coming back within <see cref="LinkLostGraceSeconds" /> is the automatic return, not a new arrival
    /// </summary>
    private static readonly Dictionary<ulong, float> LinkLostAt = [];

    // (NetReconnect gives up after 3 minutes, plus a world handover wait)
    private const float LinkLostGraceSeconds = 300f;

    /// <summary>
    ///     A combination of all remote chara's act states
    /// </summary>
    public int SharedActState => States.Values.Sum(s => s.LastAct);

    /// <summary>
    ///     Shared speed of all players
    /// </summary>
    // the players that told their speed (a 0 is one that did not yet: it slowed everyone, and no player at all
    // threw), upstream 6f0fda1
    public int SharedSpeed => (int)States.Values.Where(s => s.Speed > 0).Select(s => s.Speed).DefaultIfEmpty(0).Average();

    [ElinGameIOProperty("remote_chara")]
    internal static Dictionary<ulong, int> SavedRemoteCharas
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Every character a player made in this world. <see cref="SavedRemoteCharas" /> holds the one it plays
    /// </summary>
    [ElinGameIOProperty("remote_chara_roster")]
    private static Dictionary<ulong, List<int>> PlayerRosters
    {
        get => field ??= [];
        set;
    }

    public static void RemoveRemoteChara(Chara remoteChara, bool broadcast = true)
    {
        if (!core.IsGameStarted) {
            return;
        }

        remoteChara.SetNoGoal();

        pc.party?.RemoveMember(remoteChara);

        // it stays a resident of the base: taking it out made every return go through the whole FactionBranch.AddMemeber
        // again, which destroys the duplicates of a god's artifact among the people of the base (the other players)

        // on this map whatever zone it says it is in: left in the list it kept standing, and acting, there
        if (_map.charas.Contains(remoteChara)) {
            _zone.RemoveCard(remoteChara);
        } else {
            // a player on another map keeps its position there, which may not exist on this one
            remoteChara.parent = null;
            remoteChara.currentZone = null;
        }

        if (broadcast && NetSession.Instance.Connection is ElinNetHost host) {
            host.Delta.AddRemote(new CharaRemoveFromGameDelta {
                Owner = remoteChara,
            });
        }
    }

    /// <summary>
    ///     GitHub issue 58: the companions of a player nobody plays right now wait with it, off map and in the party, as
    ///     when it leaves (TakeCompanionsAlong); its next arrival brings them next to it (BringCompanions). A world saved
    ///     while a guest played kept them on the map: after a reload they followed the host, who had no say over them
    /// </summary>
    private static void ParkCompanions(List<Chara> companions, Chara player)
    {
        foreach (var companion in companions) {
            if (companion.isDead || companion.currentZone is null) {
                continue;
            }

            companion.SetNoGoal();
            if (_map is not null && _map.charas.Contains(companion)) {
                _zone.RemoveCard(companion);
            } else if (companion.parent is Zone) {
                companion.parent = null;
            }

            companion.currentZone = null;

            if (NetSession.Instance.Connection is ElinNetHost host) {
                host.Delta.AddRemote(new CharaRemoveFromGameDelta {
                    Owner = companion,
                });
            }

            EmpLog.Debug("Companion {Uid} waits with player {OwnerUid}, nobody plays it",
                companion.uid, player.uid);
        }
    }

    /// <summary>
    ///     The character of a player nobody plays right now is never on a map: it waits for its player with what
    ///     it carries. Wherever the game runs: the host, a player hosting its zone, a player alone on a map
    ///     (its world is the copy it left with, and nothing told it who stopped playing since)
    /// </summary>
    internal static void RemoveUnplayedCharas()
    {
        // a client on the map of its host is told who is there, see WorldStateSnapshot.RemoveLeftOverCharas
        if (!core.IsGameStarted || _map is null || Session.Connection is ElinNetClient) {
            return;
        }

        var played = (Session.Connection as ElinNetHost)?.ActiveRemoteCharas;

        foreach (var chara in _map.charas.ToArray()) {
            if (chara != pc && chara.GetBool("remote_chara") && played?.ContainsValue(chara) != true) {
                // a world being loaded, no session yet: its rides between players come back with them
                if (Session.Connection is null) {
                    LoadedRideTies.AddRange(CompanionHelper.PlayerRideTies(chara));
                }

                RemoveRemoteChara(chara);
            }
        }
    }

    /// <summary>
    ///     PeerConnect -> Prepare -> MoveZone -> SaveProbe
    /// </summary>
    public void PreparePlayerJoin(ISteamNetPeer peer, string? notice = null)
    {
        EmpLog.Information("Preparing player {@Peer} for joining",
            peer);

        if (!IsZoneSession) {
            GiveOrphanTo(peer.User);
            ReclaimLostCharas(peer);
        }

        // a saved character that is the host's own, or is played by another peer right now, is nobody's to hand out
        // (from upstream 6f0fda1; not its "must carry remote_chara" part: ours is set only once a join completes,
        // so a join that broke half way would lose its character)
        if (SavedRemoteCharas.TryGetValue(peer.User, out var saved) && game.cards.globalCharas.Find(saved) is { } taken &&
            (taken == pc || taken.IsPC || ActiveRemoteCharas.Any(kv => kv.Key != peer.Id && kv.Value == taken))) {
            EmpLog.Warning("Discarding saved remote chara {Uid} of {@Peer}, it is not a free player chara", saved, peer);
            SavedRemoteCharas.Remove(peer.User);
        }

        var roster = RosterOf(peer.User);
        var import = EmpConfig.Server.ImportCharacter.Value && !OfficialServer.Hosting && ImportTrusted(peer.User);
        // (a character taken to a solo save is not played here until it comes back)
        var known = SavedRemoteCharas.TryGetValue(peer.User, out var played) &&
                    game.cards.globalCharas.Find(played) is { } last && !CharaExport.IsAway(last);
        // no question at every connection: a player who comes back gets the character it played last. Asked only
        // when nothing says which one (several characters, none of them played last), when it has nobody here
        // and may bring someone, or when the host wants every joining player asked. That last one asks a first-time
        // player too, its list holding only "new character" (user 2026-10-30: on the public server, where nobody
        // may bring a character, a first join went straight to the creation screen and the rule looked dead)
        var ask = EmpConfig.Server.ChooseCharacter.Value ||
                  !known && (roster.Count > 1 || (roster.Count == 0 && import));
        // one of them is in a solo save: the list is where it comes back, or is brought back unchanged
        if (roster.Any(CharaExport.IsAway)) {
            ask = true;
        }

        // a player whose link dropped comes back as the one it was playing, never asked: it did not leave
        if (known && LinkLostAt.Remove(peer.User, out var lost) && Time.realtimeSinceStartup - lost < LinkLostGraceSeconds) {
            ask = false;
        }

        // permadeath: the character it played waits for a revive, it may play another one meanwhile
        if (PlayerRevive.Active && known && game.cards.globalCharas.Find(played) is { isDead: true }) {
            ask = true;
        }
        // not for the guests of a zone hosted by a player: they come with the character they are playing
        if (!IsZoneSession && ask) {
            // the player picks who to play, brings someone from its own saves, or makes someone new
            peer.Send(new SessionCharaSelectRequest {
                AllowImport = import,
                AllowDelete = EmpConfig.Server.DeleteCharacter.Value,
                AllowExport = ExportAllowed && ImportTrusted(peer.User),
                AtLimit = AtCharacterLimit(peer.User),
                Notice = notice,
                Charas = roster
                    .Select(c => new SessionCharaEntry {
                        Uid = c.uid,
                        Label = $"{c.Name} - {c.race.GetName()} {c.job.GetName()}, Lv {c.LV}",
                        Dead = PlayerRevive.Active && c.isDead,
                        Away = CharaExport.IsAway(c) ? c.GetStr(CharaExport.AwaySaveKey) ?? "" : null,
                        Portrait = PortraitOf(c),
                    })
                    .ToList(),
            });
            return;
        }

        // its only character here
        if (!IsZoneSession && !known && roster.Count == 1) {
            SavedRemoteCharas[peer.User] = roster[0].uid;
        }

        if (!SavedRemoteCharas.TryGetValue(peer.User, out var charaUid) ||
            game.cards.globalCharas.Find(charaUid) is not { } chara) {
            EmpLog.Debug("Remote character does not exist, request for new character generation");
            peer.Send(new SessionNewPlayerRequest());
        } else if (TrySendSoftStay(chara, peer)) {
            // zone session: a visitor that stood on this map before we ran it keeps its game (GitHub issue 46)
        } else {
            // remote character exists: where it left, else as before (see ArriveKnown)
            ArriveKnown(chara, peer, true);
        }
    }

    /// <summary>
    ///     The Steam account that plays a character, written on it when it is played (<see cref="RegisterPlayer" />):
    ///     the world itself says whose a character is, whatever its tables say
    /// </summary>
    internal const string PlayerIdKey = "emp_player_id";

    /// <summary>
    ///     A player's character still in the world (a player mark, at the base, among the players) but in nobody's
    ///     roster is this player's again: its Steam account written on it, else (written before the account was) the
    ///     player name. Issue #25: after the host lost its link while the guest was away, the guest found only "new
    ///     character", its own one still in the players tab and counted at the base, out of every list it can be
    ///     picked or deleted from
    /// </summary>
    private static void ReclaimLostCharas(ISteamNetPeer peer)
    {
        var claimed = SavedRemoteCharas.Values
            .Concat(PlayerRosters.Values.SelectMany(uids => uids))
            .Concat(PcOrphans.Keys)
            .ToHashSet();
        var id = ((ulong)peer.User).ToString();
        var name = peer.User.Name;

        foreach (var chara in game.cards.globalCharas.Values.ToList()) {
            if (chara is null || chara == pc || chara.IsPC || !chara.GetBool("remote_chara") || claimed.Contains(chara.uid) ||
                ServerAvatar.Is(chara)) {
                continue;
            }

            var owner = chara.GetStr(PlayerIdKey);
            var mine = owner.IsEmpty()
                ? !name.IsEmpty() && name != "?" && chara.GetStr(PlayerRevive.PlayerKey) == name
                : owner == id;
            if (!mine) {
                continue;
            }

            if (!PlayerRosters.TryGetValue(peer.User, out var roster)) {
                roster = PlayerRosters[peer.User] = [];
            }

            roster.Add(chara.uid);
            chara.SetStr(PlayerIdKey, id);
            EmpLog.Warning("Chara {Uid} ({Name}) of {@Peer} was in no roster: back in its own", chara.uid, chara.Name, peer);
            AdminLog($"character {chara.uid} ({chara.Name}) of {id} was in no roster: given back to its player");
        }
    }

    /// <summary>
    ///     The characters of a player in this world nobody is playing right now
    /// </summary>
    private List<Chara> RosterOf(ulong user)
    {
        if (!PlayerRosters.TryGetValue(user, out var uids)) {
            uids = PlayerRosters[user] = [];
        }

        // worlds from before the roster only know the one character
        if (SavedRemoteCharas.TryGetValue(user, out var current) && !uids.Contains(current)) {
            uids.Add(current);
        }

        return uids
            .Select(uid => game.cards.globalCharas.Find(uid))
            .Where(c => c is not null && !ActiveRemoteCharas.ContainsValue(c))
            .ToList();
    }

    /// <summary>
    ///     Net event: the player picked a character, or asks for a new one
    /// </summary>
    private void OnSessionCharaSelectResponse(SessionCharaSelectResponse response, ISteamNetPeer peer)
    {
        if (ActiveRemoteCharas.ContainsKey(peer.Id)) {
            return;
        }

        if (response.Uid == 0) {
            if (AtCharacterLimit(peer.User)) {
                PreparePlayerJoin(peer, "emp_ui_chara_limit");
                return;
            }

            EmpLog.Debug("Player {@Peer} asks for a new character", peer);
            peer.Send(new SessionNewPlayerRequest());
            return;
        }

        if (RosterOf(peer.User).Find(c => c.uid == response.Uid) is not { } chara) {
            EmpLog.Warning("Player {@Peer} picked chara {Uid} which is not one of its own", peer, response.Uid);
            PreparePlayerJoin(peer);
            return;
        }

        // in a solo save: neither played nor deleted here until it comes back (or is brought back unchanged)
        if (CharaExport.IsAway(chara)) {
            EmpLog.Warning("Player {@Peer} picked chara {Uid}, which is in a solo save", peer, chara.uid);
            PreparePlayerJoin(peer, "emp_ui_chara_away");
            return;
        }

        if (response.Delete) {
            if (EmpConfig.Server.DeleteCharacter.Value) {
                DeleteCharacter(peer.User, chara);
            }

            PreparePlayerJoin(peer, EmpConfig.Server.DeleteCharacter.Value ? "emp_ui_chara_deleted" : null);
            return;
        }

        // dead in a permadeath world: it waits for a revive at a bartender
        if (PlayerRevive.Active && chara.isDead) {
            PreparePlayerJoin(peer, "emp_ui_chara_dead");
            return;
        }

        SavedRemoteCharas[peer.User] = chara.uid;
        ArriveKnown(chara, peer, false);
    }

    /// <summary>
    ///     This player has as many characters in this world as the host allows (MaxCharacters, user 2026-10-27):
    ///     it makes or brings no other until it deletes one. Those it has are kept even past a lowered limit
    /// </summary>
    private bool AtCharacterLimit(ulong user)
    {
        var max = EmpConfig.Server.MaxCharacters.Value;
        if (max <= 0) {
            return false;
        }

        RosterOf(user);
        return PlayerRosters[user].Count(uid => game.cards.globalCharas.Find(uid) is not null) >= max;
    }

    /// <summary>
    ///     The portrait the game shows for a character (its own picture, or the one of its kind), for the player's
    ///     list of characters. Empty when it has none
    /// </summary>
    private static string PortraitOf(Chara chara)
    {
        try {
            return chara.GetIdPortrait() ?? "";
        } catch (Exception ex) {
            EmpLog.Debug(ex, "No portrait for chara {Uid}", chara.uid);
            return "";
        }
    }

    /// <summary>
    ///     Every character the players made in this world, for the host's list (upstream 1f932a8, remote characters
    ///     tab): its account, the character, and whether someone plays it now. The world's own player is not one
    /// </summary>
    internal List<(ulong User, Chara Chara, bool Playing)> SavedCharacters()
    {
        var list = new List<(ulong, Chara, bool)>();
        foreach (var user in PlayerRosters.Keys.Union(SavedRemoteCharas.Keys).ToList()) {
            var uids = PlayerRosters.TryGetValue(user, out var roster) ? roster.ToList() : [];
            if (SavedRemoteCharas.TryGetValue(user, out var current) && !uids.Contains(current)) {
                uids.Add(current);
            }

            foreach (var uid in uids) {
                if (game.cards.globalCharas.Find(uid) is { } chara && chara != pc) {
                    list.Add((user, chara, ActiveRemoteCharas.ContainsValue(chara)));
                }
            }
        }

        return list;
    }

    /// <summary>
    ///     The host deletes a player's character from its world (upstream 1f932a8): one nobody plays right now, the
    ///     same way as its player would (<see cref="DeleteCharacter" />)
    /// </summary>
    internal bool HostDeletes(ulong user, int uid)
    {
        if (SavedCharacters().Find(s => s.User == user && s.Chara.uid == uid) is not { Chara: { } chara, Playing: false }) {
            return false;
        }

        AdminLog($"delete character {uid} ({chara.Name}, Lv {chara.LV}) of {user}");
        DeleteCharacter(user, chara);
        return true;
    }

    /// <summary>
    ///     A player deletes one of its characters in this world, not played right now (player request,
    ///     2026-10-14: a guest who made several had no way to clear them). It goes with what it carries; its
    ///     companions stay in the world, following nobody
    /// </summary>
    private void DeleteCharacter(ulong user, Chara chara)
    {
        if (PlayerRosters.TryGetValue(user, out var roster)) {
            roster.Remove(chara.uid);
        }

        if (SavedRemoteCharas.TryGetValue(user, out var played) && played == chara.uid) {
            SavedRemoteCharas.Remove(user);
        }

        PlayerStandings.Remove(chara.uid);
        LeaveSpots.Remove(chara.uid);
        foreach (var companion in game.cards.globalCharas.Values.Where(c => c is not null && c.CompanionOwnerUid == chara.uid).ToList()) {
            companion.SetCompanionOwner(null);
            companion.party?.RemoveMember(companion);
        }

        EmpLog.Information("Player character {Uid} ({Name}, Lv {Level}) deleted by its player", chara.uid, chara.Name, chara.LV);
        DestroyPlayerChara(chara);
    }

    /// <summary>
    ///     A player's character leaves this world for good. It is a member of the base (MakeAlly), and the game's
    ///     Card.Destroy refuses a card of the player's faction with a log line only: deleted that way, it stayed in the
    ///     world, among the residents and the players, counted in the base (issue #25). Out of the base, the party and
    ///     the player marks first, then destroyed
    /// </summary>
    internal static void DestroyPlayerChara(Chara chara)
    {
        chara.SetBool("remote_chara", false);
        chara.SetStr(PlayerRevive.PlayerKey, null);
        chara.SetStr(PlayerIdKey, null);
        chara.party?.RemoveMember(chara);
        EClass.Home.FindBranch(chara)?.RemoveMemeber(chara);
        if (chara.IsPCFaction) {
            chara.SetFaction(game.factions.Wilds);
        }

        if (!chara.isDestroyed) {
            chara.Destroy();
        }

        chara.RemoveGlobal();
    }

    /// <summary>
    ///     A character brought from a save is whatever that save says: any level, any item (security audit 2026-10-27,
    ///     item 14). Where strangers come in (TrustEnforced) only a player the host vouched for brings one: an admin,
    ///     or one let in with "allow"
    /// </summary>
    internal static bool ImportTrusted(ulong user)
    {
        return !ElinDelta.TrustEnforced || Admins.ContainsKey(user) || Members.ContainsKey(user);
    }

    /// <summary>
    ///     Net event: the player brings the character of one of its own saves
    /// </summary>
    private void OnSessionCharaImportResponse(SessionCharaImportResponse response, ISteamNetPeer peer)
    {
        if (ActiveRemoteCharas.ContainsKey(peer.Id)) {
            return;
        }

        if (IsZoneSession || !EmpConfig.Server.ImportCharacter.Value || OfficialServer.Hosting) {
            EmpLog.Warning("Player {@Peer} sent a character while bringing one is not allowed", peer);
            PreparePlayerJoin(peer);
            return;
        }

        if (!ImportTrusted(peer.User)) {
            EmpLog.Warning("Refused the character brought by {@Peer}: not vouched for by the host on a public server", peer);
            AdminLog($"refused a character brought by {(ulong)peer.User}: not an admin nor let in with allow");
            PreparePlayerJoin(peer);
            return;
        }

        if (!ElinDelta.TrustEnforced && !Admins.ContainsKey(peer.User) && !Members.ContainsKey(peer.User)) {
            EmpLog.Information("Would have refused the character brought by {@Peer} on a public server", peer);
        }

        Chara chara;
        try {
            chara = response.Chara.Decompress<Chara>();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not read the character brought by player {@Peer}", peer);
            PreparePlayerJoin(peer, "emp_ui_chara_import_fail");
            return;
        }

        // a character that left this world for a solo save: it comes back only with the token of its trip, in the
        // place of the one waiting here (council 2026-10-27), never as a plain import (a copy of the solo save
        // would bring it twice)
        RosterOf(peer.User);
        var stamp = chara.GetStr(CharaExport.StampKey);
        Chara? away = null;
        if (!stamp.IsEmpty()) {
            away = PlayerRosters[peer.User]
                .Select(uid => game.cards.globalCharas.Find(uid))
                .FirstOrDefault(c => c is not null && c.GetStr(CharaExport.AwayKey) == stamp);
            if (away is null) {
                EmpLog.Information("Player {@Peer} brings {Name} with a token that is not of a trip under way", peer, chara.Name);
                AdminLog($"refused return of {chara.Name} by {peer.User}: its trip is over or not from this world");
                PreparePlayerJoin(peer, "emp_ui_chara_export_stale");
                return;
            }
        } else {
            if (AtCharacterLimit(peer.User)) {
                PreparePlayerJoin(peer, "emp_ui_chara_limit");
                return;
            }

            // one copy of a save's character per player: a second one would be the same items twice
            if (PlayerRosters[peer.User].Any(uid =>
                    game.cards.globalCharas.Find(uid)?.GetStr(CharaImport.SourceKey) == response.Source)) {
                EmpLog.Information("Player {@Peer} already brought the character of {Source}", peer, response.Source);
                PreparePlayerJoin(peer, "emp_ui_chara_import_twice");
                return;
            }
        }

        try {
            CharaImport.Adopt(chara, response.Source, away);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not take in the character brought by player {@Peer}", peer);
            PreparePlayerJoin(peer, "emp_ui_chara_import_fail");
            return;
        }

        if (away is not null) {
            ReplaceAway(peer.User, away, chara);
        }

        EmpLog.Information("Player {@Peer} brings {Name} (Lv {Level}) from its save {Source}",
            peer, chara.Name, chara.LV, response.Source);

        // before the save probe, which hands a missing standing the one of a new player
        PlayerStandings[chara.uid] = [response.Fame, response.Karma];
        SavedRemoteCharas[peer.User] = chara.uid;
        // adds the character now played to the roster
        RosterOf(peer.User);

        // (one back from a solo save is no newcomer: its team's base, if any, is where it goes)
        var isNew = away is null;
        SendSaveProbe(chara, peer, () => SendToSpawnZone(chara, peer, isNew));
    }

    /// <summary>
    ///     The character is played by that peer from now on: in the party, on the host's map, in the player list.
    ///     Shared by the copy of the world (<see cref="SendSaveProbe" />) and the return without one
    ///     (RegisterSoftRejoin)
    /// </summary>
    private void RegisterPlayer(Chara chara, ISteamNetPeer peer)
    {
        // register before any SetAI
        ActiveRemoteCharas[peer.Id] = chara;
        if (peer is Steam.SteamNetPeer joined) {
            joined.Trusted = true;
        }

        chara.MakeAlly();
        chara.MoveZone(pc.currentZone);
        chara.SetBool("remote_chara", true);
        chara.SetStr(PlayerRevive.PlayerKey, peer.User.Name ?? "?");
        // (whose it is, kept in the world: see ReclaimLostCharas)
        chara.SetStr(PlayerIdKey, ((ulong)peer.User).ToString());
        GiveAxeToRemotePlayer(chara);

        var state = States[peer.Id] = new() {
            Index = peer.Id,
            User = peer.User,
            CharaUid = chara.uid,
        };

        CardCache.Add(chara);
        CardCache.CacheContainer(chara.things);

        Session.CurrentPlayers.Add(state);
    }

    /// <summary>
    ///     Send a save snapshot for replication
    /// </summary>
    // joins within this time of the last copy share the next one (council 2026-10-27, 50 players): each copy of the
    // world and the map freezes the host, and players come back together after a restart
    private const float JoinBatchSeconds = 0.5f;
    private float _lastJoinCopy = float.MinValue;
    private readonly List<(Chara Chara, ISteamNetPeer Peer, Action? After)> _joinQueue = [];

    /// <summary>
    ///     The player plays its character from now on, and gets a copy of the world and of the host map. Alone it gets
    ///     them at once; right after another join it waits for the next copy (JoinBatchSeconds), shared by all who
    ///     came meanwhile. <paramref name="after" /> is what goes to it after both copies
    /// </summary>
    public void SendSaveProbe(Chara chara, ISteamNetPeer peer, Action? after = null)
    {
        EmpLog.Information("Sending save probe to player {@Peer} for replication",
            peer);

        RegisterPlayer(chara, peer);
        _joinQueue.Add((chara, peer, after));

        if (_joinJob is null && Time.realtimeSinceStartup - _lastJoinCopy >= JoinBatchSeconds) {
            SendJoinCopies();
        }
    }

    private void UpdateJoinCopies()
    {
        if (_joinJob is { Task.IsCompleted: true }) {
            FinishJoinCopies();
        }

        if (_joinJob is null && _joinQueue.Count > 0 && Time.realtimeSinceStartup - _lastJoinCopy >= JoinBatchSeconds) {
            SendJoinCopies();
        }
    }

    private void SendJoinCopies()
    {
        _lastJoinCopy = Time.realtimeSinceStartup;

        // (gone meanwhile, or playing another character since)
        var joins = _joinQueue
            .Where(j => j.Peer.IsConnected && ActiveRemoteCharas.TryGetValue(j.Peer.Id, out var played) && played == j.Chara)
            .ToList();
        _joinQueue.Clear();
        if (joins.Count == 0) {
            return;
        }

        // the bench reads this line: what the join cost everyone on the server (stage 2 of PLAN_100_players)
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // what is already done is in the copies below: told to everyone before them, these players would
        // otherwise replay it on top of a world that has it
        Delta.RefreshBuffer();
        WorldStateDeltaUpdate();

        // the map in the same frame as the world, not when the player asks for it once the world is loaded:
        // nothing happens between the two copies, and what happens after both is kept there until the map is
        // loaded (ElinDeltaManager.HoldForIncomingMap). The characters of the players are in the world only:
        // what they picked up or put on between the two copies never reached this player
        if (EmpConfig.Server.FastJoinsAndSaves.Value) {
            StartJoinCopies(joins, watch);
            return;
        }

        var world = SaveDataProbe.PackWorld();
        var map = _zone is { } zone ? ZoneDataResponse.Create(zone) : null;
        EmpLog.Information("World and map {ZoneFullName} sent together to {Count} player(s)",
            _zone?.ZoneFullName, joins.Count);

        foreach (var (chara, peer, _) in joins) {
            peer.Send(NetSession.Instance.Rules);
            peer.Send(new SaveDataProbe { Game = world, RemoteCharaUid = chara.uid, Lore = IsZoneSession ? null : TeamKnowledge.For(chara),
                Story = IsZoneSession ? null : TeamOwnStory.For(chara) });
            if (map is not null) {
                peer.Send(map);
            } else {
                EmpLog.Warning("No active map to send with the world to player {@Peer}", peer);
            }
        }

        TellJoined(joins);

        EmpLog.Information("Join prepared in {Main} ms main thread, {Worker} ms worker ({Count} player(s))",
            watch.ElapsedMilliseconds, 0, joins.Count);

        // counted from the end of the copies: joins held back by their freeze share the next one
        _lastJoinCopy = Time.realtimeSinceStartup;
    }

    // what goes to the players who joined right behind their copies
    private void TellJoined(List<(Chara Chara, ISteamNetPeer Peer, Action? After)> joins)
    {
        if (_zone is { } here) {
            Session.Lobby.Current[EmpLobbyData.CurrentZone] = here.NameWithLevel;
        }

        TellMarriages();
        foreach (var (chara, peer, _) in joins) {
            TellAffinities(chara, peer.Id);
        }

        TellTeams();
        TellModeration();
        Helper.PublicVouch.Ask(this, joins.Select(j => j.Peer.Id));

        foreach (var join in joins) {
            join.After?.Invoke();
        }
    }

    // FastJoinsAndSaves (stage 2 of PLAN_100_players): on a real-size world most of a join's freeze was the
    // compression of the copies (and a MessagePack copy of the map per player). The game is still read in its own
    // frame, as text and bytes; their compression runs beside it, and what is sent to these players meanwhile
    // is held behind their copies (SteamNetPeer.Hold), so they get everything in the order they did in one frame
    private static readonly SteamNetSerializer JoinSerializer = new();

    private sealed record JoinJob(
        List<(Chara Chara, ISteamNetPeer Peer, Action? After)> Joins,
        List<(TeamLore? Lore, StoryBundle? Story)> Own,
        string World,
        ZoneDataResponse.Captured? Map,
        long MainMs,
        System.Threading.Tasks.Task<(LZ4Bytes World, byte[]? Map, long Ms)> Task);

    private JoinJob? _joinJob;

    private static (LZ4Bytes World, byte[]? Map, long Ms) PackJoin(string world, ZoneDataResponse.Captured? map)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var game = LZ4Bytes.CreateFromBytes(System.Text.Encoding.UTF8.GetBytes(world));
        // one MessagePack copy of the map for all who joined together, not one per player
        var bytes = map is null ? null : JoinSerializer.Serialize(map.Pack());
        return (game, bytes, watch.ElapsedMilliseconds);
    }

    private void StartJoinCopies(List<(Chara Chara, ISteamNetPeer Peer, Action? After)> joins, System.Diagnostics.Stopwatch watch)
    {
        // read in this frame, as SendJoinCopies does: the game is only touched here
        var world = LZ4Bytes.ToJson(EClass.game);
        var map = _zone is { } zone ? ZoneDataResponse.Capture(zone) : null;
        var own = joins.Select(j => IsZoneSession ? ((TeamLore?)null, (StoryBundle?)null) : (TeamKnowledge.For(j.Chara), TeamOwnStory.For(j.Chara)))
            .ToList();

        foreach (var (_, peer, _) in joins) {
            (peer as SteamNetPeer)?.Hold();
        }

        var task = System.Threading.Tasks.Task.Run(() => PackJoin(world, map));
        // (set first: a join step that throws must not leave these players held for ever)
        _joinJob = new(joins, own, world, map, watch.ElapsedMilliseconds, task);

        // held: these go right behind the copies
        TellJoined(joins);
    }

    private static List<byte[]> JoinCopies(Chara chara, TeamLore? lore, StoryBundle? story,
        (LZ4Bytes World, byte[]? Map, long Ms) packed, ISteamNetPeer peer)
    {
        List<byte[]> first = [
            JoinSerializer.Serialize(NetSession.Instance.Rules),
            JoinSerializer.Serialize(new SaveDataProbe { Game = packed.World, RemoteCharaUid = chara.uid, Lore = lore, Story = story }),
        ];
        if (packed.Map is { } map) {
            first.Add(map);
        } else {
            EmpLog.Warning("No active map to send with the world to player {@Peer}", peer);
        }

        return first;
    }

    private void FinishJoinCopies()
    {
        if (_joinJob is not { } job) {
            return;
        }

        _joinJob = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        (LZ4Bytes World, byte[]? Map, long Ms) packed;
        try {
            packed = job.Task.Result;
        } catch (Exception ex) {
            // the same copies, packed here: the held messages are about this very world
            EmpLog.Warning(ex, "Join copies could not be packed beside the game, packed in it");
            packed = PackJoin(job.World, job.Map);
        }

        for (var i = 0; i < job.Joins.Count; i++) {
            var (chara, peer, _) = job.Joins[i];
            var (lore, story) = job.Own[i];
            List<byte[]> first;
            try {
                first = JoinCopies(chara, lore, story, packed, peer);
            } catch (Exception ex) {
                // released all the same: what was held behind the copies is not kept for ever
                EmpLog.Error(ex, "Join copies for player {@Peer} could not be made", peer);
                first = [];
            }

            if (peer is SteamNetPeer steam) {
                steam.Release(first);
            } else {
                foreach (var bytes in first) {
                    peer.Send(bytes);
                }
            }
        }

        // (the same line as SendJoinCopies: the bench waits for it)
        EmpLog.Information("World and map {ZoneFullName} sent together to {Count} player(s), packed beside the game",
            job.Map?.ZoneFullName, job.Joins.Count);
        EmpLog.Information("Join prepared in {Main} ms main thread, {Worker} ms worker ({Count} player(s))",
            job.MainMs + watch.ElapsedMilliseconds, packed.Ms, job.Joins.Count);

        // counted from the end of the copies, as SendJoinCopies does
        _lastJoinCopy = Time.realtimeSinceStartup;
    }

    /// <summary>
    ///     Net event: Client finished character generation and is ready for save probe
    /// </summary>
    private void OnSessionNewPlayerResponse(SessionNewPlayerResponse response, ISteamNetPeer peer)
    {
        EmpLog.Information("Received remote chara creation from player {@Peer}",
            peer);

        // sent twice: the character it plays would stay on the map with nobody playing it
        if (ActiveRemoteCharas.ContainsKey(peer.Id)) {
            return;
        }

        // (the choice screen asks the host first: a game that sends one anyway is past the limit)
        if (AtCharacterLimit(peer.User)) {
            EmpLog.Warning("Player {@Peer} sent a new character past the limit of {Max}", peer, EmpConfig.Server.MaxCharacters.Value);
            PreparePlayerJoin(peer, "emp_ui_chara_limit");
            return;
        }

        Chara chara;
        try {
            chara = response.Chara.Decompress<Chara>();
            AdoptNewPlayerChara(chara);
        } catch (Exception ex) {
            // back to its choice screen with a notice: left as it was, the player waited for ever (a character the
            // host could not read, 100-player bench 2026-10-28)
            EmpLog.Error(ex, "Player {@Peer} sent a new character this world cannot take", peer);
            PreparePlayerJoin(peer, "emp_ui_chara_import_fail");
            return;
        }

        SavedRemoteCharas[peer.User] = chara.uid;
        // adds the character now played to the roster
        RosterOf(peer.User);

        SendSaveProbe(chara, peer, () => SendToSpawnZone(chara, peer, true));
    }

    /// <summary>
    ///     A character just made on the creation screen of another game becomes a character of this world, with what
    ///     a new game gives its player
    /// </summary>
    private static void AdoptNewPlayerChara(Chara chara)
    {
        chara.SetBool(CINT.IsPC, false);
        chara.SetBool("emp_creating", true);
        game.cards.AssignUID(chara);
        // its 90 days without a death penalty start now, not with the world (CharaReviveDelta.IsProtected)
        chara.SetInt(CharaReviveDelta.BornKey, player.stats.days + 1);

        var host = pc;
        try {
            player.chara = chara;

            chara.hp = chara.MaxHP;
            chara.SetFaith(game.religions.list[0]);
            chara.elements.SetBase(SKILL.strategy, 1);
            chara.elements.SetBase(ABILITY.AI_Meditate, 1);
            chara.elements.SetBase(ABILITY.ActQuickCraft, 1);
            chara.elements.SetBase(ABILITY.AI_SelfHarm, 1);
            chara.elements.SetBase(ABILITY.ActPray, 1);
            foreach (var e in chara.elements.dict.Values.ToList()) {
                if (e.Value == 0) {
                    chara.elements.Remove(e.id);
                    continue;
                }
                if (e.HasTag("primary")) {
                    e.vTempPotential = Mathf.Max(30, (e.ValueWithoutLink - 8) * 7);
                }
            }

            foreach (var slot in chara.body.slots) {
                chara.body.Unequip(slot);
            }
            chara.things.DestroyAll();
            chara.elements.CheckSkillActions();
            chara.hunger.value = 30;
            chara.CalculateMaxStamina();
            chara.stamina.Set(chara.stamina.max / 2);
            chara.Refresh();

            // as a new game does, and nothing more: a purse was added here, which a player who starts a world
            // does not have
            player.CreateEquip();
        } finally {
            chara.SetBool("emp_creating", false);
            player.chara = host;
        }
    }

    /// <summary>
    ///     Request a specific client to reconnect for full synchronization
    /// </summary>
    public void RequestClientReconnect(int peerIndex)
    {
        if (!States.ContainsKey(peerIndex)) {
            EmpLog.Warning("Cannot request reconnect: peer {PeerIndex} not found",
                peerIndex);
            return;
        }

        foreach (var peer in Socket.Peers) {
            if (peer.Id == peerIndex) {
                EmpLog.Information("Requesting reconnect for peer {@Peer}",
                    peer);
                peer.Send(SessionReconnectRequest.Current);
                return;
            }
        }

        EmpLog.Warning("Cannot request reconnect: peer {PeerIndex} not connected",
            peerIndex);
    }

    private static void GiveAxeToRemotePlayer(Chara chara)
    {
        if (!chara.GetBool("remote_axe_given")) {
            try {
                chara.SetBool("emp_creating", true);
                chara.AddThing("axe");
            } finally {
                chara.SetBool("emp_creating", false);
            }
            chara.SetBool("remote_axe_given", true);
        }
    }

    [ElinPostLoad]
    private static void RemoveLeftOverCharas(GameIOContext? context)
    {
        // a world taken over from another player: our own character first, then the game starts again from that save.
        // One frame later: the tables of the save (who plays whom) are read after this hook, here they are still
        // those of the game played before. Never on a server nobody plays at, never for a client
        if (Session.Transport is null && !EmpServer.Requested) {
            var loaded = game;
            core.actionsNextFrame.Add(() => {
                if (core.game != loaded || Session.Transport is not null) {
                    return;
                }

                // opened from the copy a guest kept of it: one handover more than that copy, written by the save
                // below, or by the first save of the session when no character is exchanged
                var takenOver = WorldTakeover.Opened(out var handover);
                if (takenOver) {
                    WorldHandoverCount[0] = System.Math.Max(HandoverNumber, handover) + 1;
                    EmpLog.Information("World taken over from a copy: handover {Handover}", HandoverNumber);
                }

                // a world a server left in a crash holds its stand-in as the local character (ServerAvatar): its owner
                // gets its character back, loaded again as below; anyone else takes the place as of any world taken
                // over, TakeOverPc drops the stand-in then
                if (GiveBackToOwner(LocalUser)) {
                    var (back, backCloud) = (Game.id, game.isCloud);
                    if (game.Save(false, true)) {
                        core.actionsNextFrame.Add(() => Game.Load(back, backCloud));
                        return;
                    }

                    EmpLog.Warning("The world could not be saved after the owner's character took its place back, played as it is");
                }

                if (!TakeOverPc()) {
                    // (not when the creation screen of a new player just took the place of the game)
                    if (takenOver && scene.mode != Scene.Mode.Title) {
                        game.Save(false, true);
                    }

                    // the right character is in place: the session opens without a click on "Start Server"
                    EmpAutoHost.OpenSession();
                    return;
                }

                // not saved: played as it is, loading again would exchange again
                var (id, cloud) = (Game.id, game.isCloud);
                if (game.Save(false, true)) {
                    core.actionsNextFrame.Add(() => Game.Load(id, cloud));
                }
            });
        }

        // a server plays no character: the owner's is exchanged for a stand-in before the session opens (and the world
        // loaded again, as above, when the exchange was saved), see ServerAvatar. One frame later for the same reason
        if (Session.Transport is null && ServerAvatar.Active) {
            ServerAvatar.Settled = false;
            var loaded = game;
            core.actionsNextFrame.Add(() => {
                var again = false;
                if (core.game == loaded && Session.Transport is null) {
                    try {
                        again = MigrateToServerAvatar();
                    } catch (System.Exception ex) {
                        // the server is the old one then, with the owner's character at the base
                        EmpLog.Warning(ex, "Server: the exchange of characters for the stand-in failed");
                    }
                }

                if (again) {
                    var (id, cloud) = (Game.id, game.isCloud);
                    core.actionsNextFrame.Add(() => Game.Load(id, cloud));
                } else {
                    ServerAvatar.Settled = true;
                }
            });
        }

        IEnumerable<Chara> excluded = Session.Connection is ElinNetHost host
            ? host.ActiveRemoteCharas.Values
            : [];

        // not ourselves: a client hosting a zone session is a remote chara in the world it copied
        var currentRemoteCharas = game.cards.globalCharas.Values
            .Where(c => c != pc && c.GetBool("remote_chara"));

        foreach (var chara in currentRemoteCharas.Except(excluded).ToList()) {
            LoadedRideTies.AddRange(CompanionHelper.PlayerRideTies(chara));
            // its companions wait with it (GitHub issue 58). Not in a guest's world copy: the host tells it who is
            // where. Read from the world, not from the party its upload carries: that copy is its own game's
            List<Chara> companions = Session.Connection is ElinNetClient
                ? []
                : game.cards.globalCharas.Values.Where(c => c is not null && c.CompanionOwnerUid == chara.uid && c.IsCompanionOf(chara)).ToList();
            RemoveRemoteChara(chara);
            ParkCompanions(companions, chara);
        }

        pc.party?.members.RemoveAll(c => c is null);
        pc.party?.uidMembers.RemoveAll(uid => pc.party?.members.Find(c => c.uid == uid) is null);
    }
}