using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using UnityEngine;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    /// <summary>
    ///     The connected players that are not on the host map (see <see cref="IsAway" />): peer id and name
    /// </summary>
    internal IEnumerable<(int Id, string Name)> AwayPeers
    {
        get {
            foreach (var peer in Socket.Peers) {
                if (IsAway(peer)) {
                    yield return (peer.Id, peer.User.Name);
                }
            }
        }
    }

    /// <summary>
    ///     Council 10: every connected player is on the world map, on the host's or on its own copy of it. Then
    ///     only a jump of the date is everyone's (see RemoteTravelRegionPatch). A player still loading a map,
    ///     moving between two, or visiting another player is not travelling
    /// </summary>
    /// <param name="idleIsAway">
    ///     an idle player does not hold the others back (Travel Together's message). False for Personal Pace's leads:
    ///     an idle player in a town is not on the world map, and its date must not jump (user 2026-11-17, "days go by
    ///     in an instant" while chatting)
    /// </param>
    internal bool AllOnWorldMap(bool idleIsAway = true)
    {
        // A server's own character is a hidden stand-in that nobody sees (council 2026-10-28): it is no traveller, so
        // the players alone are asked, and the map it stands on does not count
        var serverOnly = Helper.ServerAvatar.Active;
        if (serverOnly ? Socket.Peers.Count == 0 : _zone is not { IsRegion: true }) {
            return false;
        }

        foreach (var peer in Socket.Peers) {
            // on this map
            if (ActiveRemoteCharas.ContainsKey(peer.Id)) {
                if (serverOnly && _zone is not { IsRegion: true }) {
                    return false;
                }

                continue;
            }

            // away from the keyboard: the others do not wait for it (council 2026-10-26)
            if (idleIsAway && Patches.PlayerIdle.IsIdle(peer.Id)) {
                continue;
            }

            // ponytail: a dead player away from the host still counts, the host is not told it died there
            if (!_departed.Contains(peer.Id) || _guests.ContainsKey(peer.Id) ||
                !_leases.TryGetValue(peer.Id, out var zones) || zones.Count == 0 ||
                zones.Keys.Any(uid => game.spatials.Find(uid) is not { IsRegion: true })) {
                return false;
            }
        }

        return true;
    }

    // Travel Together: what the players were last told of AllOnWorldMap, when to look again and to tell again
    private bool _toldAllOnWorldMap;
    private float _nextAllOnWorldMapCheck;
    private float _nextAllOnWorldMapTell;

    /// <summary>
    ///     Council 2026-10-23, Travel Together: the players learn whether everyone is on the world map, which lets
    ///     their steps move the date as the host's do. Looked at twice a second, told when it changes and every
    ///     5 s (a player who just joined)
    /// </summary>
    private void UpdateWorldMapTogether()
    {
        var now = Time.realtimeSinceStartup;
        var together = NetSession.Instance.Rules.WorldMapTime == WorldMapTime.TravelTogether;
        // (Personal Pace: the leads are paid whatever the mode is now, once there are any)
        if (now < _nextAllOnWorldMapCheck || !core.IsGameStarted || (!together && WorldMapLeads.Count == 0)) {
            return;
        }

        _nextAllOnWorldMapCheck = now + 0.5f;
        var all = AllOnWorldMap();
        UpdateLeads(AllOnWorldMap(false), now);
        if (!together || (all == _toldAllOnWorldMap && now < _nextAllOnWorldMapTell)) {
            return;
        }

        (_toldAllOnWorldMap, _nextAllOnWorldMapTell) = (all, now + 5f);
        Delta.AddRemote(new WorldMapTogetherDelta { All = all });
    }

    /// <summary>
    ///     The name of that player and the character it plays on this map (0 when it is away): told with the hours
    ///     of its trip
    /// </summary>
    internal (string Name, int Uid) TravellerOf(int peerId)
    {
        var name = Socket.Peers.FirstOrDefault(p => p.Id == peerId)?.User.Name ?? "?";
        return (name, ActiveRemoteCharas.TryGetValue(peerId, out var chara) ? chara.uid : 0);
    }
}
