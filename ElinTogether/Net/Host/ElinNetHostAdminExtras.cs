using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Models;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Admin extras (council 2026-10-28, host box "Moderators, freeze and jail"): a moderator who can only mute and
///     kick, freeze (the game's own paralysis, kept on the player's character until released) and jail (sent to the
///     zone an admin chose, then frozen). The host's own player is never the target, as with the other orders
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Steam ids the host made moderators: they mute, unmute and kick (not the host, not an admin, not another
    ///     moderator)
    /// </summary>
    [ElinGameIOProperty("mod_moderators")]
    internal static Dictionary<ulong, int> Moderators
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Steam id -> 1 frozen, 2 jailed (frozen too). Kept in the world save: a rejoining player stays frozen
    /// </summary>
    [ElinGameIOProperty("mod_frozen")]
    internal static Dictionary<ulong, int> Frozen
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     [the uid of the zone jailed players are sent to, 0 when none was chosen]
    /// </summary>
    [ElinGameIOProperty("mod_jail_zone")]
    internal static int[] JailZone
    {
        get => field ??= [0];
        set;
    }

    /// <summary>
    ///     The Steam id of the player at that peer index, 0 for none (the host's own index 0 is the host)
    /// </summary>
    internal ulong UserOf(int peerId)
    {
        return peerId == 0 ? Helper.ServerAvatar.HostUser : Socket.Peers.FirstOrDefault(p => p.Id == peerId) is { } peer ? (ulong)peer.User : 0UL;
    }

    internal static bool AdminExtrasOn => EmpConfig.Server.AdminExtras.Value;

    internal string MakeModerator(ulong user, bool on)
    {
        if (PeerHostedServer) {
            return "refused: a peer-hosted world has no moderators of its keeper's making";
        }

        if (!AdminExtrasOn) {
            return "Moderators, freeze and jail are off (host box)";
        }

        if (on) {
            if (user == Helper.ServerAvatar.HostUser || Admins.ContainsKey(user)) {
                return "The host and the admins need no moderator role";
            }

            Moderators[user] = 1;
        } else if (!Moderators.Remove(user)) {
            return $"{user} was not a moderator";
        }

        AdminLog($"{(on ? "mod" : "unmod")} {user}");
        TellModeration();
        return $"{user} {(on ? "is now a moderator" : "is no longer a moderator")}";
    }

    /// <summary>
    ///     Freeze that player (or jail it: <paramref name="jail" />, sent to the jail zone first)
    /// </summary>
    internal string Freeze(ulong user, bool jail = false)
    {
        if (!AdminExtrasOn) {
            return "Moderators, freeze and jail are off (host box)";
        }

        if (user == Helper.ServerAvatar.HostUser) {
            return "The host cannot be frozen";
        }

        if (jail) {
            if (game.spatials.Find(JailZone[0]) is not Zone cell) {
                return "No jail zone: stand in the zone to use and choose it first (setjail)";
            }

            if (Socket.Peers.FirstOrDefault(p => (ulong)p.User == user) is not { } peer) {
                return "not playing";
            }

            SendDeltaTo(peer.Id, new TeamSpawnDelta { ZoneUid = cell.uid });
        }

        Frozen[user] = jail ? 2 : 1;
        AdminLog($"{(jail ? "jail" : "freeze")} {user}");
        TellModeration();
        KeepFrozen();
        return $"{user} is {(jail ? "jailed" : "frozen")}";
    }

    internal string Release(ulong user)
    {
        if (!Frozen.Remove(user)) {
            return $"{user} was not frozen";
        }

        AdminLog($"release {user}");
        if (Socket.Peers.FirstOrDefault(p => (ulong)p.User == user) is { } peer &&
            ActiveRemoteCharas.GetValueOrDefault(peer.Id)?.GetCondition<ConParalyze>() is { } paralysis) {
            paralysis.Kill();
        }

        TellModeration();
        return $"{user} is released";
    }

    /// <summary>
    ///     The zone the order's sender stands in becomes the jail (a map: a region is not a place to lock anyone in)
    /// </summary>
    internal string SetJail(Zone? zone)
    {
        if (!AdminExtrasOn) {
            return "Moderators, freeze and jail are off (host box)";
        }

        if (zone is null || zone.IsRegion) {
            return "The jail must be a map, not the world map";
        }

        JailZone[0] = zone.uid;
        AdminLog($"setjail {zone.uid} {zone.ZoneFullName}");
        return $"The jail is {zone.ZoneFullName}";
    }

    /// <summary>
    ///     Every half second (UpdateRemotePlayerStates): a frozen player's character wears the game's paralysis. The
    ///     host adds it like any condition a monster would give, the player's game learns it as it learns that
    ///     one; it is put back when it wore off
    /// </summary>
    private void KeepFrozen()
    {
        if (Frozen.Count == 0 || IsZoneSession) {
            return;
        }

        foreach (var peer in Socket.Peers) {
            if (!Frozen.ContainsKey(peer.User) || ActiveRemoteCharas.GetValueOrDefault(peer.Id) is not { isDead: false } chara) {
                continue;
            }

            if (!AdminExtrasOn) {
                continue;
            }

            if (chara.GetCondition<ConParalyze>() is not { } paralysis || paralysis.value < 50) {
                chara.AddCondition<ConParalyze>(1000, true);
            }
        }
    }
}
