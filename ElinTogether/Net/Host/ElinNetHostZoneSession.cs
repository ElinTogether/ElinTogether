using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;

namespace ElinTogether.Net;

/// <summary>
///     Zone session host: a client simulating an away zone hosts the players visiting it,
///     next to its own link with the real host, see NetSession.ZoneSession
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Local udp port of zone sessions, one block of ports per debug identity
    /// </summary>
    private const ushort ZoneSessionPortBase = 55600;

    /// <summary>
    ///     Guests leaving on purpose, their host link uploads their character, see ZoneGuestLeave
    /// </summary>
    private readonly HashSet<int> _leavingGuests = [];

    internal static ushort ZoneSessionPort =>
#if DEBUG
        (ushort)(ZoneSessionPortBase + 10 * EmpConfig.Dev.Identity.Value);
#else
        ZoneSessionPortBase;
#endif

    /// <summary>
    ///     Steam virtual port of a zone session on the account of a hidden server: the server listens on 0
    /// </summary>
    internal const int ZoneSessionVirtualPort = 1;

    internal void StartZoneServer(bool localUdp, bool hiddenOwner = false)
    {
        Stop();
        StopWorldStateUpdate();

        // no Steam lobby: guests are announced by the host, see RegisterGuest
        if (hiddenOwner) {
            // our link to the server is the loopback one, the friends come through Steam on a virtual port of ours
            Socket.StartServerUdp(ZoneSessionPort, alsoSteam: true, vport: ZoneSessionVirtualPort);
        } else if (localUdp) {
            Socket.StartServerUdp(ZoneSessionPort);
        } else {
            Socket.StartServerSdr();
        }

        Scheduler.Subscribe(DisconnectInactive, 1);
        Scheduler.Subscribe(RemoveStaleIntegrityCheck, 2);

        var selfState = States[0] = new() {
            Index = 0,
            User = ServerAvatar.HostUser,
            CharaUid = player.uidChara,
        };

        Session.Player = pc;
        Session.Self = selfState;
        Session.CurrentPlayers.Clear();
        Session.CurrentPlayers.Add(selfState);
        RefreshSharedSpeed();

        CardCache.CacheCurrentZone();

        StartWorldStateUpdate();

        EmpLog.Information("Zone session started in {ZoneFullName}",
            _zone.ZoneFullName);
    }

    /// <summary>
    ///     Expect a player announced by the host, with its character as the host knows it
    /// </summary>
    internal void RegisterGuest(ZoneGuestRequest request)
    {
        UserData user = request.GuestUser;

        if (ReplaceRemoteChara(user, request.Chara, true) is { } guest) {
            // placed next to it once it stands here, see BringCompanions
            ReplaceCompanions(request.Companions, guest.uid);
        }

        // it stays on its tile (the map changed hands under it) or arrives by the way it walks in, not next to us,
        // see OnZoneDataReceivedResponse
        if (request.Stood is not null || request.Arrival is not null) {
            _returnSpots[request.GuestUser] = (request.ZoneUid, (Point?)request.Stood, request.Arrival,
                UnityEngine.Time.unscaledTime + ReturnSpotSeconds, request.StoodStale);
        } else {
            _returnSpots.Remove(request.GuestUser);
        }

        SteamNetManager.ConnectionKeys[user] = "zone_guest";

        EmpLog.Information("Expecting guest {RemoteIdentity}",
            user);
    }

    /// <summary>
    ///     Characters of the guests, simulated here, uploaded with checkpoints and releases
    /// </summary>
    internal Dictionary<ulong, List<LZ4Bytes>> CollectGuestCompanions()
    {
        var companions = new Dictionary<ulong, List<LZ4Bytes>>();

        foreach (var (peerId, chara) in ActiveRemoteCharas) {
            if (States.TryGetValue(peerId, out var state)) {
                companions[state.User] = CompanionHelper.TravellingWith(chara).Select(c => LZ4Bytes.Create(c)).ToList();
            }
        }

        return companions;
    }

    internal Dictionary<ulong, LZ4Bytes> CollectGuestCharas()
    {
        var charas = new Dictionary<ulong, LZ4Bytes>();

        foreach (var (peerId, chara) in ActiveRemoteCharas) {
            if (States.TryGetValue(peerId, out var state)) {
                charas[state.User] = LZ4Bytes.Create(chara);
            }
        }

        return charas;
    }

    /// <summary>
    ///     Net event: a guest leaves the zone, apply its last actions and send their results before it goes
    /// </summary>
    private void OnZoneGuestLeave(ZoneGuestLeave leave, ISteamNetPeer peer)
    {
        if (!IsZoneSession) {
            return;
        }

        WorldStateDeltaProcess();
        // what it was handed and still holds out of any bag goes with its character (issue 48)
        ThingRequest.ReturnDanglingOf(peer.Id);
        Delta.RefreshBuffer();
        WorldStateDeltaUpdate();

        _leavingGuests.Add(peer.Id);
        PendingRebind.ReleasePeer(peer.Id);

        if (States.Remove(peer.Id, out var state)) {
            Session.CurrentPlayers.Remove(state);
        }

        if (ActiveRemoteCharas.Remove(peer.Id, out var chara)) {
            RemoveRemoteChara(chara);
            TakeCompanionsAlong(chara);
        }

        Broadcast(SessionPlayersSnapshot.Create());

        peer.Send(new ZoneGuestLeft {
            ZoneUid = leave.ZoneUid,
        });

        EmpLog.Information("Guest {@Peer} left the zone",
            peer);
    }

    /// <summary>
    ///     A guest dropped: unless it left on purpose, its character only exists here, checkpoint it now
    /// </summary>
    private void OnZoneGuestDisconnecting(ISteamNetPeer peer)
    {
        if (_leavingGuests.Remove(peer.Id) || !ActiveRemoteCharas.ContainsKey(peer.Id)) {
            return;
        }

        // closing on our own, leaving the zone: the release carries the guests
        if (Session.ZoneSession != this) {
            return;
        }

        EmpLog.Warning("Guest {@Peer} dropped, sending its character to the host",
            peer);

        (Session.Transport as ElinNetClient)?.SendTravelCheckpoint();
    }

    /// <summary>
    ///     Last guest gone: back to simulating the zone alone
    /// </summary>
    private void CloseZoneSessionIfEmpty(ISteamNetPeer leaving)
    {
        // by reference: a guest coming back holds the same id on its new link while the old one closes
        if (Socket.Peers.Any(p => !ReferenceEquals(p, leaving))) {
            return;
        }

        EmpLog.Information("Last guest left, closing the zone session");
        EndSession("emp_zone_session_empty");
    }
}
