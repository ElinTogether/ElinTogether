using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The dedicated public server binds its players' world service keys to their Steam ids before the world is kept
///     by its players (PLAN_peer_hosted_server 2.1, Q4): it asks each player who joins for the public half of its key
///     with a fresh nonce (EtWorldKeyDelta), checks the player's signature of that nonce (it holds the key: nobody
///     binds someone else's key to their own Steam id), and vouches for it at the service under the Steam id Steam
///     authenticated for that link, so a stranger who registered someone else's Steam id first is never the one the
///     service trusts. One answer per ask, a few vouches a minute at most. Only on the dedicated public server (the
///     public server key on this PC), with the world admin key on this PC (ElinMP/etworld_admin.key) and an
///     etworld: depot, and the host box "Vouch players at the world service" on. Nothing is asked of the player.
///     A player answers any host that asks: the answer is a public key bound to the Steam id that host already knows
/// </summary>
internal static class PublicVouch
{
    private const int VouchesPerMinute = 20;
    private const float AnswerSeconds = 120f;

    // keys already vouched in this run, by Steam id (asked again at each join, sent once)
    private static readonly Dictionary<ulong, string> _done = [];
    // the nonce each asked peer must sign, and until when (one answer each)
    private static readonly Dictionary<int, (string Nonce, float Until)> _asked = [];
    private static readonly Queue<float> _sent = new();
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
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) {
                random.GetBytes(bytes);
            }

            var nonce = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            _asked[peer] = (nonce, Time.realtimeSinceStartup + AnswerSeconds);
            host.SendDeltaTo(peer, new EtWorldKeyDelta { Nonce = nonce });
        }
    }

    /// <summary>
    ///     On a player's game: the server asked for its key
    /// </summary>
    internal static void Answer(ElinNetClient client, string nonce)
    {
        if (!Regex.IsMatch(nonce, "^[0-9a-f]{32}$")) {
            return;
        }

        try {
            var (key, sig) = SaveDepot.EtWorldSign(EtWorldDepot.VouchMessage(nonce));
            client.Delta.AddRemote(new EtWorldKeyDelta { Key = key, Sig = sig });
        } catch (Exception ex) {
            // (a damaged key file is never replaced: no answer, the server vouches next time)
            EmpLog.Warning(ex, "World service: the key could not be read to answer the server");
        }
    }

    /// <summary>
    ///     On the server: a player's answer to an ask of this server, signed with that key, vouched under the Steam id
    ///     of its link (never one it names)
    /// </summary>
    internal static void OnKey(ElinNetHost host, int peerId, string key, string sig)
    {
        if (host.IsZoneSession || !Wanted) {
            return;
        }

        // one answer per ask, in time
        if (!_asked.TryGetValue(peerId, out var asked)) {
            EmpLog.Debug("World service: a key answer of peer {Peer} that was not asked for, dropped", peerId);
            return;
        }

        _asked.Remove(peerId);
        key = key.Trim().ToLowerInvariant();
        var steam = host.UserOf(peerId);
        if (Time.realtimeSinceStartup > asked.Until || steam == 0 || steam == ServerAvatar.HostUser ||
            !Regex.IsMatch(key, "^[0-9a-f]{64}$") || !EtWorldDepot.VouchSigned(key, asked.Nonce, sig.Trim().ToLowerInvariant())) {
            EmpLog.Warning("World service: the key answer of {Steam} was late, unusable or not signed by that key, not vouched", steam);
            return;
        }

        var now = Time.realtimeSinceStartup;
        lock (_gate) {
            if (_done.TryGetValue(steam, out var known) && known == key) {
                return;
            }

            while (_sent.Count > 0 && now - _sent.Peek() > 60f) {
                _sent.Dequeue();
            }

            if (_sent.Count >= VouchesPerMinute) {
                EmpLog.Debug("World service: too many vouches this minute, {Steam} is vouched at its next join", steam);
                return;
            }

            _sent.Enqueue(now);
            _done[steam] = key;
        }

        WorldAdmin.Vouch(key, steam, (status, error) => {
            switch (status) {
                case 200:
                    EmpLog.Information("World service: the key {Key} of {Steam} is vouched for", key.Substring(0, 8), steam);
                    break;
                case 409 when error == "steam_mismatch":
                    // the key is bound to another Steam id although this player holds it (it registered under another
                    // account): the mod's log only, this runs off the game's thread and admin.log is not written from it
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
