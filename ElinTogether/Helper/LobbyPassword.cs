using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using ElinTogether.Common;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;
using Steamworks;

namespace ElinTogether.Helper;

/// <summary>
///     A password a host may ask of every player who joins, friends included (box LobbyPassword, Workshop comment
///     2026-11-19). The lobby only says that one is needed (EmpLobbyData.Password), never the password; a joiner sends
///     SHA-256 of the password in its handshake (NetIntegrityResponse.LobbyPasswordHash) and the
///     host compares. The public world and a dedicated or hidden server never ask one
/// </summary>
internal static class LobbyPassword
{
    // what a joiner gave, by the host's Steam id: a rejoin or a reconnect of this run is not asked again
    private static readonly Dictionary<ulong, byte[]> Given = [];

    /// <summary>
    ///     This game, as host, asks a password of the players who join
    /// </summary>
    internal static bool Required => EmpConfig.Server.LobbyPassword.Value.Trim().Length > 0 &&
                                     !PeerHosting.Active && !EmpServer.Requested && !OfficialServer.Hosting;

    // (the password alone, not the host's Steam id: a direct join by address knows no Steam id of the host, its
    // hash never matched, bench 2026-11-20; what was given is still kept by host, see Given)
    internal static byte[] Hash(ulong host, string password)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes($"ElinTogether lobby:{password.Trim()}"));
    }

    /// <summary>
    ///     Host side: null when the joiner may come in, else the reason it is put out with
    /// </summary>
    internal static string? Refusal(byte[]? hash)
    {
        if (!Required) {
            return null;
        }

        if (hash is null) {
            return EmpDisconnectInfo.LobbyPassword;
        }

        var expected = Hash(SteamUser.GetSteamID().m_SteamID, EmpConfig.Server.LobbyPassword.Value);
        var diff = hash.Length ^ expected.Length;
        for (var i = 0; i < Math.Min(hash.Length, expected.Length); i++) {
            diff |= hash[i] ^ expected[i];
        }

        return diff == 0 ? null : EmpDisconnectInfo.LobbyPasswordWrong;
    }

    /// <summary>
    ///     Host side: the lobby says whether a password is needed (written again when the host changes it)
    /// </summary>
    internal static void MarkLobby(LobbyData lobby)
    {
        if (lobby.IsValid && lobby.IsOwner) {
            lobby[EmpLobbyData.Password] = Required ? "1" : "0";
        }
    }

    internal static bool Shown(LobbyData lobby) => lobby[EmpLobbyData.Password] == "1";

    /// <summary>
    ///     Joiner side: what this player gave for that host, null when nothing was asked yet
    /// </summary>
    // (a direct join by address names its host by no Steam id: the last password given is sent)
    internal static byte[]? For(ulong host) => Given.TryGetValue(host, out var hash) ? hash : _last;

    private static byte[]? _last;

    internal static bool Known(ulong host) => Given.ContainsKey(host);

    internal static void Forget(ulong host)
    {
        if (Given.TryGetValue(host, out var hash) && hash == _last) {
            _last = null;
        }

        Given.Remove(host);
    }

    /// <summary>
    ///     Joiner side: asks the password of that host once, then goes on (a cancel stops there)
    /// </summary>
    internal static void Ask(ulong host, Action then, string ask = "emp_ui_lobby_password_ask")
    {
        Dialog.InputName(ask, "", (cancel, text) => {
            if (cancel || text.Trim().Length == 0) {
                EmpLog.Information("Lobby password not given for host {Host}", host);
                return;
            }

            Given[host] = _last = Hash(host, text);
            then();
        }).input.field.characterLimit = 64;
    }
}
