using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     The roster of the server (user 2026-10-30, the public server, where players travel alone a lot: "the players
///     list must show all the players of the server"): every connected player with a character, on this map or away,
///     and the host itself unless it is a dedicated server's stand-in. Sent to every player, away ones included, when it
///     changes and every few seconds for the pings. CurrentPlayers stays this map's list, the sync reads that one
/// </summary>
internal partial class ElinNetHost
{
    // the pings move every second: sent again for them at this pace only, a change of who/where goes at once
    private const float RosterRefreshSeconds = 6f;

    private int _rosterKey;
    private float _rosterSentAt = float.MinValue;

    internal List<RosterEntry> BuildRoster()
    {
        var list = new List<RosterEntry>();
        if (EClass.pc is { } pc && !Helper.ServerAvatar.IsUid(pc.uid)) {
            var self = States.GetValueOrDefault(0);
            list.Add(new() {
                Peer = 0,
                Steam = self is null ? 0 : (ulong)self.User,
                Name = self?.User.Name ?? "",
                CharaUid = pc.uid,
                CharaName = pc.Name,
                Level = pc.LV,
                Team = Helper.Teams.TeamOf(pc.uid),
                Where = _zone?.NameWithLevel ?? "",
                Idle = Patches.PlayerIdle.IsIdle(0),
            });
        }

        foreach (var peer in Socket.Peers.ToArray()) {
            if (PlayerChara(peer.Id) is not { } chara || Helper.ServerAvatar.IsUid(chara.uid)) {
                // (still choosing or loading its character: not playing yet)
                continue;
            }

            var away = IsAway(peer);
            var state = States.GetValueOrDefault(peer.Id);
            var ping = state is null ? 0 : Mathf.RoundToInt(state.AvgPingMs > 0 ? state.AvgPingMs : state.LastPingMs);
            list.Add(new() {
                Peer = peer.Id,
                Steam = (ulong)peer.User,
                Name = peer.User.Name ?? "",
                CharaUid = chara.uid,
                CharaName = chara.Name,
                Level = chara.LV,
                Team = Helper.Teams.TeamOf(chara.uid),
                Where = (away ? AwayZoneOf(peer.Id) : _zone)?.NameWithLevel ?? chara.currentZone?.NameWithLevel ?? "",
                Away = away,
                PingMs = ping,
                Idle = Patches.PlayerIdle.IsIdle(peer.Id),
            });
        }

        return list;
    }

    /// <summary>
    ///     Where an away player stands: the map of another player it visits, else the last map it was lent (two while
    ///     it moves between them, the next one granted last)
    /// </summary>
    private Zone? AwayZoneOf(int peerId)
    {
        if (_guests.TryGetValue(peerId, out var visit) && game.spatials.Find(visit.ZoneUid) is Zone visited) {
            return visited;
        }

        return _leases.TryGetValue(peerId, out var zones) && zones.Count > 0 ? game.spatials.Find(zones.Keys.Last()) as Zone : null;
    }

    /// <summary>
    ///     Scheduler job (1 Hz): the host's own roster, and to every connected player when it changed (who, where, level,
    ///     team, idle) or the refresh time is up (the pings)
    /// </summary>
    private void UpdateServerRoster()
    {
        if (IsZoneSession || !core.IsGameStarted) {
            return;
        }

        var roster = BuildRoster();
        Session.SetServerRoster(roster);

        var key = RosterKey(roster);
        var now = Time.realtimeSinceStartup;
        if (key == _rosterKey && now - _rosterSentAt < RosterRefreshSeconds) {
            return;
        }

        _rosterKey = key;
        _rosterSentAt = now;
        // only players playing (as listed): a peer still validating or choosing its character is told nobody's
        // Steam account nor where they stand (it gets the roster once it plays: it is in it, the roster changed)
        var peers = Socket.Peers.Where(peer => ActiveRemoteCharas.ContainsKey(peer.Id) || IsAway(peer)).ToArray();
        if (peers.Length == 0) {
            return;
        }

        // (serialized once; straight to each peer, away ones too: SendByMap would keep it from them)
        var bytes = Socket.Serializer.Serialize(new WorldStateDeltaList {
            DeltaList = [new ServerRosterDelta { Players = roster }],
        });
        foreach (var peer in peers) {
            peer.Send(bytes);
        }
    }

    private static int RosterKey(List<RosterEntry> roster)
    {
        unchecked {
            var hash = 17;
            foreach (var p in roster) {
                hash = hash * 31 + p.Peer;
                hash = hash * 31 + p.CharaUid;
                hash = hash * 31 + p.Level;
                hash = hash * 31 + p.Team;
                hash = hash * 31 + (p.Away ? 1 : 0) + (p.Idle ? 2 : 0);
                hash = hash * 31 + p.Where.GetHashCode();
                hash = hash * 31 + p.Name.GetHashCode();
            }

            return hash;
        }
    }
}
