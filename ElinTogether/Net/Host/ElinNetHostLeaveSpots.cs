using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

/// <summary>
///     Host box ReturnWhereLeft (user 2026-11-02, public server: "players should come back to the position where they
///     left the server last time"): the map and the tile each player character left the world from are kept in the
///     save, noted when its player leaves and at every save of the world (a crash keeps the last one). Its next arrival
///     takes it back there when that map still exists and is open to it, and the tile can be walked on; otherwise, and
///     for a character that never left from anywhere yet (a new player, a world from before this), the usual start:
///     the spawn zone rule, or the entrance of the host's map. Noted whatever the box says, so that ticking it later
///     finds every player's place
/// </summary>
internal partial class ElinNetHost
{
    private const int LeaveZone = 0;
    private const int LeaveX = 1;
    private const int LeaveZ = 2;

    /// <summary>
    ///     Player chara uid -> [zone uid, x, z] of where it last left the world. An older save has none
    /// </summary>
    [ElinGameIOProperty("leave_spots")]
    internal static Dictionary<int, int[]> LeaveSpots
    {
        get => field ??= [];
        set;
    }

    // peer id -> the map it stood on at its last checkpoint away and its tile there (its character's copy on this game
    // is the one that checkpoint brought)
    private readonly Dictionary<int, (int ZoneUid, int X, int Z)> _stoodAway = [];

    /// <summary>
    ///     A checkpoint of a player away: where it stands on the map it holds
    /// </summary>
    private void NoteStoodAway(ZoneLeaseRelease checkpoint, ISteamNetPeer peer, Chara? chara)
    {
        // (not Point.IsValid: it measures against this game's map, and a world map tile is often past its size)
        if (chara?.pos is { x: >= 0, z: >= 0 } pos && checkpoint.StoodZoneUid > 0) {
            _stoodAway[peer.Id] = (checkpoint.StoodZoneUid, pos.x, pos.z);
        }
    }

    /// <summary>
    ///     Where that player stands now, kept for its next arrival. <paramref name="leaving" />: it is leaving, a place
    ///     not known (on its way somewhere, visiting another player's map) forgets the older one, which would be stale
    /// </summary>
    private void NoteLeaveSpot(ISteamNetPeer peer, bool leaving)
    {
        if (IsZoneSession || !SavedRemoteCharas.TryGetValue(peer.User, out var uid)) {
            return;
        }

        (int ZoneUid, int X, int Z)? spot = null;
        if (_settled.Contains(peer.Id) && _zone is { } here && ActiveRemoteCharas.TryGetValue(peer.Id, out var chara) &&
            chara.uid == uid && chara.IsInActiveMap) {
            spot = (here.uid, chara.pos.x, chara.pos.z);
        } else if (_stoodAway.TryGetValue(peer.Id, out var stood) && _leases.TryGetValue(peer.Id, out var held) &&
                   held.ContainsKey(stood.ZoneUid)) {
            spot = stood;
        }

        if (spot is { } s) {
            LeaveSpots[uid] = [s.ZoneUid, s.X, s.Z];
        } else if (leaving && IsAway(peer) && LeaveSpots.Remove(uid)) {
            // (away with no checkpoint yet, or visiting another player's map: the older place is stale. A player that
            // drops while it is still arriving keeps the one it came back to)
            EmpLog.Debug("Player {@Peer} leaves from no known place, its last one is forgotten", peer);
        }
    }

    /// <summary>
    ///     Before a save: where every player stands, so that a crash after it keeps that
    /// </summary>
    internal void NoteAllLeaveSpots()
    {
        if (IsZoneSession) {
            return;
        }

        foreach (var peer in Socket.Peers.ToArray()) {
            NoteLeaveSpot(peer, false);
        }
    }

    /// <summary>
    ///     The way back to where a player left. Handled: the place is taken (on the host's map the return spot is set
    ///     before the map is sent; elsewhere ZoneUid is where its game travels once the copies are in), nothing else
    ///     decides where it starts
    /// </summary>
    private readonly record struct LeaveReturn(bool Handled, int ZoneUid = 0, int X = -1, int Z = -1);

    private LeaveReturn PrepareLeaveSpot(Chara chara, ISteamNetPeer peer)
    {
        if (IsZoneSession || !EmpConfig.Server.ReturnWhereLeft.Value || chara.isDead ||
            !LeaveSpots.TryGetValue(chara.uid, out var spot) || spot is not { Length: >= 3 }) {
            return default;
        }

        if (game.spatials.Find(spot[LeaveZone]) is not Zone zone || zone.destryoed) {
            EmpLog.Information("Player {@Peer} left from zone {ZoneUid}, which is gone: the usual start", peer, spot[LeaveZone]);
            return default;
        }

        var (x, z) = (spot[LeaveX], spot[LeaveZ]);
        if (zone == _zone) {
            var at = new Point(x, z);
            if (!at.IsValid || !at.IsInBounds || at.IsBlocked || (ServerAvatar.Is(pc) && pc.pos.Equals(at))) {
                EmpLog.Information("Player {@Peer} left from {X},{Z} of {ZoneFullName}, which cannot be walked on now: the usual start",
                    peer, x, z, zone.ZoneFullName);
                return default;
            }

            // (a fresher one noted in this run, a dropped link, stays)
            if (!_returnSpots.ContainsKey(peer.User)) {
                _returnSpots[peer.User] = (zone.uid, at, null, UnityEngine.Time.unscaledTime + ReturnSpotSeconds, false);
            }

            EmpLog.Information("Player {@Peer} comes back where it left, {X},{Z} of {ZoneFullName}", peer, x, z, zone.ZoneFullName);
            return new(true);
        }

        // never when nobody travels alone (everyone follows the host), a quest's map (gone once left), a land closed to
        // it, or a map another player holds alone
        if (!NetSession.Instance.Rules.AllowIndependentTravel || zone.instance is not null || _questZones.Contains(zone.uid) || Teams.EntryDenied(chara, zone) is not null ||
            (!zone.IsRegion && _leases.Any(kv => kv.Key != peer.Id && kv.Value.ContainsKey(zone.uid)))) {
            EmpLog.Information("Player {@Peer} left from {ZoneFullName}, which is not open to it now: the usual start", peer, zone.ZoneFullName);
            return default;
        }

        EmpLog.Information("Player {@Peer} goes back where it left, {X},{Z} of {ZoneFullName}", peer, x, z, zone.ZoneFullName);
        return new(true, zone.uid, x, z);
    }

    /// <summary>
    ///     A player comes back with a character of this world: the copies, then where it starts. Where it left (host box
    ///     ReturnWhereLeft), else the map it held when its link dropped (<paramref name="linkReturn" />) and the spawn zone
    ///     rule, as before
    /// </summary>
    private void ArriveKnown(Chara chara, ISteamNetPeer peer, bool linkReturn)
    {
        var back = PrepareLeaveSpot(chara, peer);
        SendSaveProbe(chara, peer, () => {
            if (!back.Handled) {
                SendToSpawnZone(chara, peer, false);
                if (linkReturn) {
                    ReturnToAwayZone(peer);
                }

                return;
            }

            // (the map it held when its link dropped is that same place, with its tile; so is the place of a server's
            // owner noted at the exchange of characters, and the spawn zone is spent for this hosting)
            _awayAt.Remove(peer.User);
            _spawned.Add(peer.User);
            if (ServerAvatar.Active && (ulong)ServerAvatarTable[AvatarOwnerUser] == (ulong)peer.User) {
                ServerAvatarTable[AvatarOwnerZone] = 0;
            }

            if (back.ZoneUid != 0) {
                SendDeltaTo(peer.Id, new TeamSpawnDelta {
                    ZoneUid = back.ZoneUid,
                    X = back.X,
                    Z = back.Z,
                });
            }
        });
    }
}
