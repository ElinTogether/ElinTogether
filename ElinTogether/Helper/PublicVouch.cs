using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;

namespace ElinTogether.Helper;

/// <summary>
///     The dedicated public server binds its players' world service keys to their Steam ids before the world is kept
///     by its players (PLAN_peer_hosted_server 2.1, Q4): it asks each player who joins for the public half of its key
///     (EtWorldKeyDelta) and vouches for it at the service under the Steam id Steam authenticated for that link, so a
///     stranger who registered someone else's Steam id first is never the one the service trusts. Only on the
///     dedicated public server (the public server key on this PC), with the world admin key on this PC
///     (ElinMP/etworld_admin.key) and an etworld: depot, and the host box "Vouch players at the world service" on.
///     Nothing is asked of the player; the answer is a public key only
/// </summary>
internal static class PublicVouch
{
    // keys already vouched in this run, by Steam id (asked again at each join, sent once)
    private static readonly Dictionary<ulong, string> _done = [];
    private static readonly object _gate = new();

    internal static bool Wanted =>
        EmpConfig.Server.VouchPlayers.Value && EmpServer.Requested && OfficialServer.CanHost() && !PeerHosting.Active &&
        WorldAdmin.HasKey && WorldAdmin.Address is not null;

    /// <summary>
    ///     On the server, when players have joined
    /// </summary>
    internal static void Ask(ElinNetHost host, IEnumerable<int> peers)
    {
        if (host.IsZoneSession || !Wanted) {
            return;
        }

        foreach (var peer in peers) {
            host.SendDeltaTo(peer, new EtWorldKeyDelta());
        }
    }

    /// <summary>
    ///     On a player's game: the server asked for its key
    /// </summary>
    internal static void Answer(ElinNetClient client)
    {
        try {
            client.Delta.AddRemote(new EtWorldKeyDelta { Key = SaveDepot.EtWorldPublicKey() });
        } catch (Exception ex) {
            // (a damaged key file is never replaced: no answer, the server vouches next time)
            EmpLog.Warning(ex, "World service: the key could not be read to answer the server");
        }
    }

    /// <summary>
    ///     On the server: a player's answer, vouched under the Steam id of its link (never one it names)
    /// </summary>
    internal static void OnKey(ElinNetHost host, int peerId, string key)
    {
        if (host.IsZoneSession || !Wanted) {
            return;
        }

        key = key.Trim().ToLowerInvariant();
        var steam = host.UserOf(peerId);
        if (steam == 0 || steam == ServerAvatar.HostUser || !Regex.IsMatch(key, "^[0-9a-f]{64}$")) {
            EmpLog.Debug("World service: a key answer of peer {Peer} was not usable", peerId);
            return;
        }

        lock (_gate) {
            if (_done.TryGetValue(steam, out var known) && known == key) {
                return;
            }

            _done[steam] = key;
        }

        WorldAdmin.Vouch(key, steam, (status, error) => {
            switch (status) {
                case 200:
                    EmpLog.Information("World service: the key {Key} of {Steam} is vouched for", key.Substring(0, 8), steam);
                    break;
                case 409 when error == "steam_mismatch":
                    // the key is bound to another Steam id: whoever registered it is not the player of this link (the
                    // mod's log only: this runs off the game's thread, admin.log is not written from it)
                    EmpLog.Warning("World service: the key {Key} sent by {Steam} is bound to another Steam id, not vouched",
                        key.Substring(0, 8), steam);
                    break;
                default:
                    EmpLog.Debug("World service: vouching for {Steam} failed, {Status} {Error}", steam, status, error);
                    lock (_gate) {
                        // (tried again at the next join)
                        if (_done.TryGetValue(steam, out var mine) && mine == key) {
                            _done.Remove(steam);
                        }
                    }

                    break;
            }
        });
    }
}
