using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     The hidden server of a peer-hosted world (PeerHosting, PLAN_peer_hosted_server section 3): its admins and bans
///     are the signed lists, read at the start and every few minutes (what the save holds is only a cache, replaced),
///     no moderator is made here, the rules are the signed ones (PeerRules), and the game's own debug cheats are off.
///     Whoever keeps the world is the owner's seat, a plain guest: never an admin for keeping it
/// </summary>
internal partial class ElinNetHost
{
    private const float PeerSyncSeconds = 300f;

    // bans an admin of the signed list made during this session: kept until the store lists them (P6), forgotten at a
    // restart if it never does
    private static readonly HashSet<ulong> _sessionBans = [];
    private static float _nextPeerSync;

    /// <summary>
    ///     This game is the hidden server of a peer-hosted world
    /// </summary>
    internal static bool PeerHostedServer => PeerHosting.Active && EmpServer.Requested;

    /// <summary>
    ///     At the start of the session, before the rules are read for the players
    /// </summary>
    private static void BeginPeerHosted()
    {
        if (!PeerHostedServer) {
            return;
        }

        PeerRules.Apply();
        ForceDebugOff();
        _sessionBans.Clear();
        SyncPeerModeration(null);
        _nextPeerSync = Time.realtimeSinceStartup + PeerSyncSeconds;
    }

    /// <summary>
    ///     Every tick of the server (EmpServer): the rules put back if they moved, the lists read again now and then
    /// </summary>
    internal void PeerHostedTick()
    {
        if (!PeerHostedServer || IsZoneSession) {
            return;
        }

        PeerRules.Enforce(this);
        ForceDebugOff();
        if (Time.realtimeSinceStartup >= _nextPeerSync) {
            _nextPeerSync = Time.realtimeSinceStartup + PeerSyncSeconds;
            SyncPeerModeration(this);
        }
    }

    /// <summary>
    ///     The admins and bans of the save made the signed ones (bans made this session by a signed admin kept);
    ///     moderators emptied (the signed lists name none yet). Players now banned are put out
    /// </summary>
    private static void SyncPeerModeration(ElinNetHost? host)
    {
        var admins = PeerHosting.AdminSet();
        var bans = PeerHosting.BanSet();
        var changed = false;
        if (!new HashSet<ulong>(Admins.Keys).SetEquals(admins)) {
            Admins.Clear();
            foreach (var admin in admins) {
                Admins[admin] = 1;
            }

            changed = true;
        }

        if (Moderators.Count > 0) {
            Moderators.Clear();
            changed = true;
        }

        foreach (var user in Bans.Keys.Where(u => !bans.Contains(u) && !_sessionBans.Contains(u)).ToList()) {
            Bans.Remove(user);
            changed = true;
        }

        foreach (var user in bans.Where(u => !Bans.ContainsKey(u))) {
            Bans[user] = "";
            changed = true;
        }

        if (!changed) {
            return;
        }

        AdminLog($"peer-hosted: signed lists applied, {Admins.Count} admins, {Bans.Count} banned");
        EmpLog.Information("Peer-hosted world: signed lists applied, {Admins} admins, {Bans} banned", Admins.Count, Bans.Count);
        if (host is null) {
            return;
        }

        foreach (var peer in host.Socket.Peers.Where(p => Bans.ContainsKey(p.User)).ToList()) {
            host.Socket.Disconnect(peer, EmpDisconnectInfo.Banned);
        }

        host.TellModeration();
    }

    /// <summary>
    ///     A ban made by a signed admin in this session (RunAdminOrder): kept through the next reading of the lists
    /// </summary>
    private static void NoteSessionBan(ulong user, bool banned)
    {
        if (!PeerHostedServer) {
            return;
        }

        if (banned) {
            _sessionBans.Add(user);
        } else {
            _sessionBans.Remove(user);
        }
    }

    /// <summary>
    ///     Elin's own debug cheats change the world for everyone when the game that runs it has them: off on the
    ///     hidden server of a peer-hosted world (whether a release game can turn them on is not known; this costs
    ///     nothing either way)
    /// </summary>
    private static void ForceDebugOff()
    {
        if (EClass.debug is not { } debug) {
            return;
        }

        debug.godMode = debug.godCraft = debug.godFood = debug.ignoreBuildRule = debug.ignoreWeight = false;
        debug.autoIdentify = debug.allAbility = debug.allHomeSkill = debug.allArt = debug.allRecipe = false;
        debug.returnAnywhere = debug.instaReturn = debug.testThingQuality = debug.unlimitedInterest = false;
        debug.marryAnytime = debug.alwaysFavFood = debug.maxQuests = false;
    }
}
