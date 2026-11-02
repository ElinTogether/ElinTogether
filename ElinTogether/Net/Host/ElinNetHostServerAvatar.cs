using System;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;

namespace ElinTogether.Net;

/// <summary>
///     A server that plays no character, see ServerAvatar. A world opened by `-empserver` has its owner's character
///     as the local one, planted at the base: it is exchanged, once and silently, with a stand-in made for the
///     server (Exchange, the same swap as a world taken over by another player), and the owner's character becomes
///     the character of an absent player that comes back when its player joins by its Steam id. Nothing is asked
///     of anyone and nothing is copied: the character keeps its uid, so the teams, the stories and the rosters
///     that know it by uid need no change
/// </summary>
internal partial class ElinNetHost
{
    private const int AvatarSchema = 0;
    private const int AvatarOwnerUser = 1;
    private const int AvatarOwnerUid = 2;
    private const int AvatarOwnerZone = 3;
    private const int AvatarOwnerX = 4;
    private const int AvatarOwnerZ = 5;
    private const int AvatarUid = 6;

    /// <summary>
    ///     [schema (0 until the first exchange, 1 after), the Steam id of the owner of the world, its character's
    ///     uid, the uid of the zone it was in and where (x, z) when the server took its place, the stand-in's uid].
    ///     The owner's own character is told by PcOwners/SavedRemoteCharas like any player's; this keeps what only
    ///     the server needs: that a backup was made, and where the owner was, for its first join
    /// </summary>
    [ElinGameIOProperty("server_avatar")]
    internal static long[] ServerAvatarTable
    {
        get => field is { Length: 7 } ? field : field = new long[7];
        set;
    }

    /// <summary>
    ///     The server just loaded a world: the owner's character is exchanged for the stand-in, unless it already is
    ///     (a world the server saved before, or a crash save). True when the world was saved with the exchange and
    ///     must be loaded again, so that the game starts from it as from any save
    /// </summary>
    internal static bool MigrateToServerAvatar()
    {
        if (!ServerAvatar.Active) {
            return false;
        }

        var table = ServerAvatarTable;
        if (ServerAvatar.Is(pc)) {
            // a world the server saved already: nothing to exchange, only the table to keep right
            table[AvatarSchema] = Math.Max(table[AvatarSchema], 1);
            table[AvatarUid] = pc.uid;
            RetireServerOwnChara(game.cards.globalCharas.Find((int)table[AvatarOwnerUid]));
            MirrorOwnerStanding();
            ParkStandIn(true);
            return false;
        }

        var home = pc.homeBranch?.owner;
        if (home is null || pc.party is not { } party || pc.isDead) {
            EmpLog.Warning("Server: no stand-in made for the owner's character, it has no base, no party or is dead");
            return false;
        }

        // the save as it was, kept with the game's own backups, on the first exchange only. Without it nothing is
        // exchanged: the server is the old one, with the owner's character planted at the base
        if (table[AvatarSchema] == 0) {
            try {
                GameIO.MakeBackup(new GameIndex { id = Game.id, cloud = game.isCloud });
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Server: no backup before the exchange of characters, nothing exchanged");
                return false;
            }
        }

        // whose the owner's character is: the player the save says, else a player who comes without a character
        // gets it back (GiveOrphanTo)
        var owner = PcOwner();
        var former = pc;
        var zone = former.currentZone;
        var (x, z) = (former.pos.x, former.pos.z);

        var standIn = CharaGen.Create("chara");
        try {
            standIn.SetBool("emp_creating", true);
            standIn.SetGlobal();
            standIn.SetFaction(EClass.Home);
            standIn.homeZone = former.homeZone;
            standIn.c_altName = "Server";
            standIn.SetBool(ServerAvatar.Flag, true);
            standIn.isHidden = true;
            standIn.hp = standIn.MaxHP;
        } finally {
            standIn.SetBool("emp_creating", false);
        }

        // (a player's books, so that the swap puts the owner's standing and random quests in theirs)
        PlayerStandings[standIn.uid] = [0, NewPlayerKarma];
        Exchange(standIn, owner, LocalUser);

        // the stand-in waits out of the way, see ParkStandIn. A owner who was elsewhere: the game puts whoever has no
        // zone back at the base by itself (Game.OnLoad), and the next start parks it
        if (zone == home && standIn.currentZone == home) {
            ParkStandIn(false);
        } else {
            standIn.currentZone = null;
        }

        table[AvatarSchema] = Math.Max(table[AvatarSchema], 1);
        table[AvatarOwnerUser] = (long)owner;
        table[AvatarOwnerUid] = former.uid;
        table[AvatarOwnerZone] = zone?.uid ?? 0;
        table[AvatarOwnerX] = x;
        table[AvatarOwnerZ] = z;
        table[AvatarUid] = standIn.uid;
        RetireServerOwnChara(former);
        MirrorOwnerStanding();
        EmpLog.Information("Server: the character {Owner} of player {User} is a player's now, the server plays {StandIn}",
            former.uid, owner, standIn.uid);

        // saved with the exchange; the caller loads it again. A save that fails leaves the server on the exchanged
        // characters in memory, which the next save writes
        if (game.Save(false, true)) {
            return true;
        }

        EmpLog.Warning("Server: the world could not be saved after the exchange of characters, played as it is");
        return false;
    }

    /// <summary>
    ///     The stand-in stands where no player can walk: a tile of the map outside its playable bounds (the corner of the
    ///     margin every map has around them), else, on a map with no margin, the free tile nearest to the corner of the
    ///     bounds. The game needs the local character on the server's map; nobody sees it (ServerAvatarPatch) and no
    ///     player is put next to it any more (OnZoneDataReceivedResponse). Run at every start of a server, so a stand-in
    ///     left anywhere by an older build is moved away too. <paramref name="onMap" />: the map is shown and the
    ///     stand-in on it (moved with the map's cells), else only its tile is set (a save and a load follow)
    /// </summary>
    internal static void ParkStandIn(bool onMap)
    {
        if (!ServerAvatar.Is(pc) || _map is not { } map || pc.currentZone != _zone) {
            return;
        }

        var bounds = map.bounds;
        var spot = bounds.x > 0 && bounds.z > 0
            ? new Point(0, 0)
            : new Point(bounds.x, bounds.z).GetNearestPoint(allowBlock: false, allowChara: false);
        if (spot is not { IsValid: true } || pc.pos.Equals(spot)) {
            return;
        }

        try {
            if (onMap && map.charas.Contains(pc)) {
                map.MoveCard(spot, pc);
            } else {
                pc.pos.Set(spot);
            }

            EmpLog.Information("Server: the stand-in waits at {X},{Z} of {ZoneFullName}, out of the players' way", spot.x, spot.z, _zone.ZoneFullName);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Server: the stand-in could not be moved out of the players' way");
        }
    }

    // schema value: the owner's character is the server's own and plays nowhere (RetireServerOwnChara)
    private const long AvatarRetired = 2;

    /// <summary>
    ///     User 2026-10-30, "the server is not invisible and is considered a player": the official world is made by a
    ///     new game whose character ("Elin Together", OfficialWorld) claimed the start land. Exchanged for the stand-in,
    ///     it became the character of an absent player of the server's own account: listed among the players, given a
    ///     share of every story reward by mail, a delivery box, a seat in the stories. It is nobody's: once the
    ///     stand-in plays the seat, it is taken out of every list of players (it stays in the world, off any map, flagged
    ///     a remote character so the host never shows it, for the stand-in to give its place back to when the server
    ///     stops, see GiveBackToOwner). What was on its way to it by mail (copies of what the players earned) is
    ///     dropped. Run at every start of a server, so a world made before this is repaired by its next start. A world
    ///     a player made (any other character) is never touched
    /// </summary>
    private static void RetireServerOwnChara(Chara? own)
    {
        if (own is null || ServerAvatar.Is(own) || own == pc || !OfficialWorld.IsServerOwn(own)) {
            return;
        }

        var uid = own.uid;
        own.SetBool(OfficialWorld.ServerOwnFlag, true);
        own.SetBool("remote_chara", true);
        foreach (var user in SavedRemoteCharas.Where(p => p.Value == uid).Select(p => p.Key).ToList()) {
            SavedRemoteCharas.Remove(user);
        }

        foreach (var (user, roster) in PlayerRosters.ToList()) {
            roster.Remove(uid);
            if (roster.Count == 0) {
                PlayerRosters.Remove(user);
            }
        }

        PcOrphans.Remove(uid);

        var parcels = game.cards.listPackage.Where(t => t.GetInt(TeamMail.ParcelKey) == uid).ToList();
        foreach (var parcel in parcels) {
            game.cards.listPackage.Remove(parcel);
            parcel.Destroy();
        }

        ServerAvatarTable[AvatarSchema] = AvatarRetired;
        EmpLog.Information("Server: the world's own character {Uid} is no player ({Parcels} parcels on their way to it dropped)",
            uid, parcels.Count);
    }

    /// <summary>
    ///     The Steam id of the owner of the world a server holds (0: none)
    /// </summary>
    internal static ulong AvatarOwnerUserId()
    {
        return (ulong)ServerAvatarTable[AvatarOwnerUser];
    }

    /// <summary>
    ///     The uid of the character the owner of the world plays now: the one it plays by its Steam id (it may have
    ///     made another since the exchange), else the one that was exchanged. 0 when the server has no owner
    /// </summary>
    internal static int AvatarOwnerCharaUid()
    {
        var table = ServerAvatarTable;
        // (the server's own character, retired: the one the world was made with, whatever its account plays now)
        return table[AvatarSchema] < AvatarRetired && table[AvatarOwnerUser] != 0 &&
               SavedRemoteCharas.TryGetValue((ulong)table[AvatarOwnerUser], out var uid)
            ? uid
            : (int)table[AvatarOwnerUid];
    }

    /// <summary>
    ///     The stand-in carries the standing of the owner in the game's own fame and karma (<c>player.fame</c>):
    ///     whatever the game reads for "the player" without asking who (the fame tax, the level of what the world
    ///     makes, the town boards) then reads the owner's. The owner's own game keeps the count; this is a copy that is
    ///     refreshed before every save and after the world's hooks. A change made to it outside a player's scope
    ///     (a deed nobody did) is dropped here, so that nothing is charged twice
    /// </summary>
    internal static void MirrorOwnerStanding()
    {
        if (!ServerAvatar.Is(pc) || AvatarOwnerCharaUid() is not (> 0 and var uid) ||
            !PlayerStandings.TryGetValue(uid, out var standing) || standing.Length < 2) {
            return;
        }

        if (player.fame == standing[StandingFame] && player.karma == standing[StandingKarma]) {
            return;
        }

        EmpLog.Debug("Server: the stand-in's fame {Fame} and karma {Karma} become the owner's {OwnerFame} and {OwnerKarma}",
            player.fame, player.karma, standing[StandingFame], standing[StandingKarma]);
        player.fame = standing[StandingFame];
        player.karma = standing[StandingKarma];
    }

    /// <summary>
    ///     The server stops, or a world it left is opened by a game that plays a character: the stand-in is the local
    ///     character, and <paramref name="me" /> is the owner of the world. The owner's character takes the place
    ///     again (the same swap as the migration, the other way), the stand-in is destroyed, and the world on disk is
    ///     the one of a game hosted from the menu. True when exchanged, the caller saves
    /// </summary>
    internal static bool GiveBackToOwner(ulong me)
    {
        var table = ServerAvatarTable;
        if (!ServerAvatar.Is(pc) || me == 0 || (ulong)table[AvatarOwnerUser] != me) {
            return false;
        }

        if (game.cards.globalCharas.Find(AvatarOwnerCharaUid()) is not { isDead: false } owner || owner == pc ||
            pc.party is null || CharaExport.IsAway(owner)) {
            EmpLog.Warning("Server: the owner's character cannot take its place back (missing, dead, away or the stand-in has no party)");
            return false;
        }

        var standIn = pc;
        Exchange(owner, 0, me);
        DropStandIn(standIn);
        EmpLog.Information("Server: the character {Owner} of player {User} is the local one again, the stand-in {StandIn} is gone",
            owner.uid, me, standIn.uid);
        return true;
    }

    /// <summary>
    ///     A stand-in that is no longer the local character leaves the world: nobody plays it, owns it, or knows it
    /// </summary>
    private static void DropStandIn(Chara standIn)
    {
        var uid = standIn.uid;
        foreach (var user in SavedRemoteCharas.Where(p => p.Value == uid).Select(p => p.Key).ToList()) {
            SavedRemoteCharas.Remove(user);
        }

        foreach (var user in PcOwners.Where(p => p.Value == uid).Select(p => p.Key).ToList()) {
            PcOwners.Remove(user);
        }

        foreach (var roster in PlayerRosters.Values) {
            roster.Remove(uid);
        }

        PcOrphans.Remove(uid);
        PlayerStandings.Remove(uid);

        // no party may be led by it once it is gone: the next load threw on the leader (2026-10-30, PartyRepair)
        if (pc is { } local && local != standIn) {
            PartyRepair.Repoint(uid, local);
        }
        PersonalQuestLogs.Remove(uid);

        // (a character of the home faction is not destroyed: it leaves the faction first)
        standIn.SetFaction(EClass.game.factions.Wilds);
        standIn.Destroy();

        // the backup stays made (schema 1): a later exchange does not make a second one
        var table = ServerAvatarTable;
        table[AvatarUid] = 0;
        table[AvatarOwnerZone] = 0;
    }

    /// <summary>
    ///     Another player took the place of a stand-in left in a world (TakeOverPc): nothing keeps it any more
    /// </summary>
    private static void DropStandInAfterTakeOver(Chara? standIn)
    {
        if (standIn is not null && standIn != pc && ServerAvatar.Is(standIn)) {
            DropStandIn(standIn);
        }
    }

    private bool _kickedForStop;

    /// <summary>
    ///     The server is asked to stop: every player is put out (once), and this says when none is left, so that the
    ///     owner's character can be given back to the world without a game playing it
    /// </summary>
    internal bool EmptiedForStop()
    {
        if (!_kickedForStop) {
            _kickedForStop = true;
            foreach (var peer in Socket.Peers.ToArray()) {
                Socket.Disconnect(peer, EmpDisconnectInfo.HostShutdown);
            }
        }

        return Socket.Peers.Count == 0 && ActiveRemoteCharas.Count == 0;
    }
}
