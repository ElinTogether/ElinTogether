using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using Steamworks;

namespace ElinTogether.Net;

/// <summary>
///     The owner of a hidden server (HiddenServer): the game that started it joins over the loopback with the Steam
///     account the server itself runs on. That seat is the owner's if, and only if, its first handshake carries the
///     token the starting game wrote for the server; the same account on the loopback without it is put out (any
///     process of the machine can reach the port). The owner is an admin of its world, as the host of a game is
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     The Steam account that showed the token (0: none did)
    /// </summary>
    internal static ulong OwnerSeat { get; private set; }

    /// <summary>
    ///     A hidden server, and a peer from this machine on the account the server runs on: the owner's seat if it
    ///     shows the token
    /// </summary>
    private static bool IsOwnerCandidate(ISteamNetPeer peer)
    {
        return EmpServer.Hidden && peer is SteamNetPeer { IsLoopback: true } &&
               (ulong)peer.User == SteamUser.GetSteamID().m_SteamID;
    }

    /// <summary>
    ///     This peer is the owner of the hidden server
    /// </summary>
    internal static bool IsOwnerSeat(ISteamNetPeer peer)
    {
        return OwnerSeat != 0 && (ulong)peer.User == OwnerSeat && peer is SteamNetPeer { IsLoopback: true };
    }

    /// <summary>
    ///     First handshake message of a peer: false (the peer is put out) when it claims the owner's seat without the
    ///     token
    /// </summary>
    private bool AcceptOwnerSeat(NetIntegrityResponse response, ISteamNetPeer peer)
    {
        if (!IsOwnerCandidate(peer)) {
            return true;
        }

        if (!HiddenServer.TokenMatches(response.OwnerToken)) {
            EmpLog.Warning("Hidden server: the loopback seat of the server's own account came without the owner's token, put out ({@Peer})", peer);
            Socket.Disconnect(peer, EmpDisconnectInfo.OwnerSeat);
            return false;
        }

        // a peer-hosted world: keeping it gives nothing (PLAN_peer_hosted_server section 3, rows 2-3). The seat is a
        // plain guest, turned away like one when the signed lists ban it, and an admin only if they name it
        if (PeerHostedServer) {
            if (Refusal(peer.User) is { } refused) {
                EmpLog.Information("Hidden server: the owner's seat {@Peer} refused: {Reason}", peer, refused);
                AdminLog($"refused owner seat {(ulong)peer.User} {peer.User.Name}: {refused}");
                Socket.Disconnect(peer, refused);
                return false;
            }

            OwnerSeat = peer.User;
            EmpLog.Information("Hidden server: {@Peer} is the owner's seat, a plain guest of this peer-hosted world", peer);
            return true;
        }

        OwnerSeat = peer.User;
        // (the host of a game administers it without being asked; the owner of this one is not "the host" any more)
        Admins[OwnerSeat] = 1;
        EmpLog.Information("Hidden server: {@Peer} is the owner's seat", peer);
        return true;
    }
}
