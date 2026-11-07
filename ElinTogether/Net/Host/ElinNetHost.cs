using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;

namespace ElinTogether.Net;

internal partial class ElinNetHost : ElinNetBase
{
    internal readonly Dictionary<int, NetPeerState> States = [];

    public override bool IsHost => true;

    /// <summary>
    ///     Listening on the local udp port: what a second game on this machine can join
    /// </summary>
    internal bool IsLocalServer => Socket.IsLocalUdp;

    /// <param name="quiet">Opened by itself at load (EmpAutoHost): friends only, and no window says so</param>
    /// <summary>
    ///     The version of the mod this world was last hosted with, kept in the save
    /// </summary>
    [ElinGameIOProperty("hosted_version")]
    private static string[] HostedVersion
    {
        get => field is { Length: 1 } ? field : field = [""];
        set;
    }

    /// <summary>
    ///     A world is hosted for the first time with this version of the mod: the save as it is on disk goes to the
    ///     game's own backups first (the ones the game's load menu restores), once. A version that goes wrong
    ///     leaves a world to come back to, without the player having thought of copying it
    /// </summary>
    private static void BackupBeforeNewVersion()
    {
        if (HostedVersion[0] == ModInfo.BuildVersion) {
            return;
        }

        try {
            GameIO.MakeBackup(new GameIndex { id = Game.id, cloud = game.isCloud });
            EmpLog.Information("World {World} hosted with {Version} for the first time (was {Before}): the save was copied to the game's backups first",
                Game.id, ModInfo.BuildVersion, HostedVersion[0].Length == 0 ? "never hosted" : HostedVersion[0]);
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "No backup of the save before hosting it with a new version of the mod");
        }

        HostedVersion[0] = ModInfo.BuildVersion;
    }

    internal void StartServer(bool localUdp = false, bool quiet = false, bool alsoSteam = false)
    {
        Stop();
        StopWorldStateUpdate();

        if (!core.IsGameStarted || player?.chara?.homeBranch?.owner is null) {
            EmpLog.Warning("Cannot start server: game not started or no land claimed");
            EmpPop.Debug("emp_ui_unclaimed_zone".lang());
            Session.ResetSession();
            return;
        }

        // a peer-hosted world opens on a hidden server only: a visible game hosting it would be a host with powers no
        // guest has (PLAN_peer_hosted_server section 3)
        if (PeerHosting.Active && !EmpServer.Requested) {
            EmpLog.Warning("Cannot start server: this peer-hosted world opens through the hidden server only");
            EmpPop.Information("emp_peer_no_classic".Loc("emp_peer_why_hidden".lang()));
            Session.ResetSession();
            return;
        }

        BackupBeforeNewVersion();
        // a counter near the end of the numbers goes down first (issues 78-81), the save just backed up
        Helper.UidRepair.Run();
        // a peer-hosted world: its signed rules, admins and bans, whatever this machine's files say
        BeginPeerHosted();
        // the official server's fixed rules, over whatever the file says
        Components.SettingsTable.ForceOfficial();
        // the headless official server: the whole preset, its admins, its message of the day (before the rules are read)
        OfficialServerStart.Run();
        RepairBillCount();
        Patches.QuestVernisRepair.Repair();
        Patches.QuestPuppyGuard.Repair();

        // nobody asked for this session: never one a stranger can find, but the players of this world come in
        // without being Steam friends of whoever hosts it today
        Session.Lobby.KnownAccount = quiet
            ? user => SavedRemoteCharas.ContainsKey(user) || PlayerRosters.ContainsKey(user) || PcOwners.ContainsKey(user)
            : null;
        Session.Lobby.CreateLobby(quiet ? SteamNetLobbyType.Friend : SteamNetLobbyType.Public, quiet: quiet);

        try {
            if (localUdp) {
                Socket.StartServerUdp(alsoSteam: alsoSteam);
                WorldHandover.AnnounceLocal(Common.EmpConstants.LocalPort);
            } else {
                Socket.StartServerSdr();
            }
        } catch {
            Session.ResetSession();
            throw;
        }

        Scheduler.Subscribe(DisconnectInactive, 1);
        Scheduler.Subscribe(RemoveStaleIntegrityCheck, 2);

        RememberPcOwner();
        KeepSavedRideTies();

        // host also registers self state
        var selfState = States[0] = new() {
            Index = 0,
            User = ServerAvatar.HostUser,
            CharaUid = player.uidChara,
        };

        // setup session states
        // (rules and player list of an earlier session do not carry over)
        Session.Rules = NetSessionRules.Default;
        Helper.ServerInfo.Arrived();
        Session.Player = pc;
        Session.Self = selfState;
        Session.CurrentPlayers.Clear();
        Session.CurrentPlayers.Add(selfState);
        RefreshSharedSpeed();

        if (quiet) {
            EmpLog.Information("Server started by itself");
        } else {
            EmpPop.Information("emp_server_started".lang());
        }

        CardCache.CacheCurrentZone();

        StartWorldStateUpdate();
    }

    protected override void RegisterPackets()
    {
        Router.ShouldReceivePacket = ShouldReceivePeerPacket;

        Router.RegisterHandler<NetIntegrityResponse>(OnNetHandshakeResponse);

        Router.RegisterHandler<SessionNewPlayerResponse>(OnSessionNewPlayerResponse);
        Router.RegisterHandler<SessionCharaSelectResponse>(OnSessionCharaSelectResponse);
        Router.RegisterHandler<SessionCharaImportResponse>(OnSessionCharaImportResponse);
        Router.RegisterHandler<SessionCharaExportRequest>(OnSessionCharaExportRequest);
        Router.RegisterHandler<SessionCharaExportResult>(OnSessionCharaExportResult);
        Router.RegisterHandler<MapDataRequest>(OnMapDataRequest);
        Router.RegisterHandler<ZoneDataReceivedResponse>(OnZoneDataReceivedResponse);
        Router.RegisterHandler<WorldStateRequest>(OnWorldStateRequest);
        Router.RegisterHandler<WorldStateDeltaList>(OnWorldStateDeltaResponse);
        Router.RegisterHandler<CharaStateSnapshot>(OnClientRemoteCharaSnapshot);

        // independent travel
        Router.RegisterHandler<ZoneLeaseRequest>(OnZoneLeaseRequest);
        Router.RegisterHandler<ZoneLeaseAck>(OnZoneLeaseAck);
        Router.RegisterHandler<ZoneLeaseRelease>(OnZoneLeaseRelease);
        Router.RegisterHandler<ZoneLeaseDecline>(OnZoneLeaseDecline);
        Router.RegisterHandler<ZoneSoftRejoinFailed>(OnZoneSoftRejoinFailed);
        Router.RegisterHandler<ZoneGuestReady>(OnZoneGuestReady);
        Router.RegisterHandler<ZoneGuestLeave>(OnZoneGuestLeave);
        Router.RegisterHandler<ShippingDeposit>(OnShippingDeposit);

        // source validation
        Router.RegisterHandler<SourceValidationResponse>(OnSourceValidationResponse);
        Router.RegisterHandler<SourceValidationContinue>(OnSourceValidationContinue);
    }

    private void Broadcast<T>(T packet)
    {
        Socket.Broadcast.Send(packet);
    }

    private const float AwayGoneSeconds = 2f;

    // away peer id -> when its link was first seen closed
    private readonly Dictionary<int, float> _awayGoneAt = [];

    protected void DisconnectInactive()
    {
        foreach (var peer in Socket.Peers) {
            if (IsAway(peer)) {
                // away players have no tick state, drop them once the transport is gone. Not at once: a player that
                // quits closes its link with its reason, which comes a little after the link shows closed; dropped
                // here first, its leave read as a lost link (2026-10-26)
                if (peer.IsConnected) {
                    _awayGoneAt.Remove(peer.Id);
                } else if (!_awayGoneAt.TryGetValue(peer.Id, out var gone)) {
                    _awayGoneAt[peer.Id] = UnityEngine.Time.realtimeSinceStartup;
                } else if (UnityEngine.Time.realtimeSinceStartup - gone > AwayGoneSeconds) {
                    _awayGoneAt.Remove(peer.Id);
                    Socket.Disconnect(peer, EmpDisconnectInfo.InactivePeer);
                }

                continue;
            }

            if (!States.TryGetValue(peer.Id, out var state)) {
                continue;
            }

            if (state.LastReceivedTick == -1) {
                continue;
            }

            // client has not been responding after 25 ticks
            if (!peer.IsConnected && Session.Tick - state.LastReceivedTick > 25) {
                Socket.Disconnect(peer, EmpDisconnectInfo.InactivePeer);
            }
        }

        // remove all left over chara
        // (not ourselves: a client hosting a zone session is a remote chara in the world it copied)
        foreach (var chara in _map.charas.ToArray()) {
            if (chara != pc && chara.GetBool("remote_chara") && !ActiveRemoteCharas.Values.Contains(chara)) {
                RemoveRemoteChara(chara);
            }
        }
    }

#region Net Events

    protected override void OnPeerConnected(ISteamNetPeer peer)
    {
        // (no wait for the Steam name: it arrives through a callback of this very thread, a spin of 0.5 s only froze
        // every join, reliability audit 2026-10-27)
        // banned, or kicked a moment ago: turned away before anything of the world is sent (ElinNetHostModeration)
        // (the owner of a hidden server is the seat of its own account on the loopback, see AcceptOwnerSeat: its
        // token is not known yet, the handshake decides; until then it is the owner's, not a stranger to turn away)
        if (!IsOwnerCandidate(peer) && Refusal(peer.User) is { } refused) {
            EmpLog.Information("Player {@Peer} refused: {Reason}", peer, refused);
            AdminLog($"refused {(ulong)peer.User} {peer.User.Name}: {refused}");
            Socket.Disconnect(peer, refused);
            return;
        }

        // the most players at once (MaxPlayers): a new one is told the server is full; a player whose link is being
        // replaced (same id), the owner's seat and the admins are never counted out
        // (a peer-hosted world's keeper may hold a lower cap of its own while its PC is slow, see LoadCap)
        if (!IsOwnerCandidate(peer) && EffectiveCap(EmpConfig.Server.MaxPlayers.Value) is > 0 and var cap && !Admins.ContainsKey(peer.User) &&
            Socket.Peers.Count(p => !ReferenceEquals(p, peer) && p.Id != peer.Id) >= cap) {
            EmpLog.Information("Player {@Peer} refused: the server is full ({Cap} players)", peer, cap);
            AdminLog($"refused {(ulong)peer.User} {peer.User.Name}: full ({cap})");
            Socket.Disconnect(peer, EmpDisconnectInfo.With(EmpDisconnectInfo.ServerFull, cap.ToString()));
            return;
        }

        // a player coming back before its dead link was noticed: a returning player keeps its peer id, and its
        // old link closing later would take the character of the new one off the map. One link per player
        foreach (var old in Socket.Peers) {
            if (!ReferenceEquals(old, peer) && old.Id == peer.Id) {
                Socket.Disconnect(old, EmpDisconnectInfo.InactivePeer);
            }
        }

        // (the game's tone in the message log: who, not the link's fields; the link goes to the mod's own log)
        EmpLog.Information("Player {@Peer} connected", peer);
        AdminLog($"join {(ulong)peer.User} {peer.User.Name}");
        EmpPop.Information(Escape("emp_player_connected".Loc(WhoIs(peer))));

        // shou lai
        BeginHandshake(peer);

#if DEBUG
        if (!IsDebugGuiActive) {
            StartDebugGui();
        }
#endif
    }

    // a player as the message log names it: its Steam name, "A player" while Steam has not told it yet
    private static string WhoIs(ISteamNetPeer peer)
    {
        return string.IsNullOrEmpty(peer.User.Name) ? "emp_player_someone".lang() : peer.User.Name;
    }

    // (a notice is read as a message template: a brace in a name would be taken for a field)
    private static string Escape(string text)
    {
        return text.Replace("{", "{{").Replace("}", "}}");
    }

    protected override void OnPeerDisconnected(ISteamNetPeer peer, string disconnectInfo)
    {
        EmpLog.Information("Player {@Peer} disconnected: {DisconnectInfo}", peer, disconnectInfo);
        AdminLog($"leave {(ulong)peer.User} {peer.User.Name}: {disconnectInfo}");
        EmpPop.Information(Escape("emp_player_disconnected".Loc(WhoIs(peer))));

        _handshakes.Remove(peer.Id);
        PendingRebind.ReleasePeer(peer.Id);
        // what it held out of any bag is back in it now, not 10 s later (issue 48)
        ThingRequest.ReturnDanglingOf(peer.Id);
        ForgetFlood(peer.Id);

        if (IsZoneSession) {
            OnZoneGuestDisconnecting(peer);
        } else {
            // where it leaves from, for its next arrival (ReturnWhereLeft): before its leases are given up
            NoteLeaveSpot(peer, true);
            _stoodAway.Remove(peer.Id);
            ReleaseLeaseOnDisconnect(peer);
        }

        // (the reason as it was given: the event only carries its text)
        if (EmpDisconnectInfo.IsLinkLost(Router.DisconnectReason)) {
            LinkLostAt[peer.User] = UnityEngine.Time.realtimeSinceStartup;
        } else {
            LinkLostAt.Remove(peer.User);
        }

        if (!IsZoneSession) {
            Patches.PlayerIdle.Forget(peer.Id);
        }

        // (its emote limit: a peer id comes back for the next player)
        ChatEmoteDelta.Forget(peer.Id);

        // the date it leaves at: what the world does after is not taken from its own quests (stage 4)
        if (States.TryGetValue(peer.Id, out var leaving)) {
            SeePlayers(leaving);
        }

        if (States.Remove(peer.Id, out var state)) {
            // Fully remove remote chara from the map (saved chara remains via ElinGameIOProperty)
            if (ActiveRemoteCharas.Remove(peer.Id, out var remoteChara)) {
                if (LinkLostAt.ContainsKey(peer.User)) {
                    KeepRideTies(peer.User, remoteChara);
                }

                RemoveRemoteChara(remoteChara);
                TakeCompanionsAlong(remoteChara);
                EmpLog.Information("Player {PlayerName} remote chara {Uid} removed from map. " +
                                   "Saved chara retained for future new connections.",
                    state.User.Name, remoteChara.uid);
            }

            Session.CurrentPlayers.Remove(state);
        }

        EmpLog.Debug("Player {PlayerName} disconnected. {Remaining} players remaining",
            state?.User.Name ?? "unknown", States.Count);

        Broadcast(SessionPlayersSnapshot.Create());

        if (_handshakes.Count == 0) {
            StopDebugGui();
        }

        if (IsZoneSession) {
            CloseZoneSessionIfEmpty(peer);
        }
    }

#endregion
}