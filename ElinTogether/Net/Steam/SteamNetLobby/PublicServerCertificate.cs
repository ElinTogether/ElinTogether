using System;
using System.Collections.Generic;
using System.IO;
using ElinTogether.Helper;
using HeathenEngineering.SteamworksIntegration;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net.Steam;

/// <summary>
///     Who gets host certificates for this game's lobby: the ETWorld client (plan P2/P5) when this game keeps the
///     public world. It is set by that client; nothing here talks to the service
/// </summary>
internal interface IHostCertificateIssuer
{
    /// <summary>This game holds the keeper lease of the public world (its lobby is meant to carry a certificate)</summary>
    bool Keeps { get; }

    /// <summary>
    ///     This game opened its Steam lobby: ask the service for a certificate naming it (POST /v1/w/public/cert
    ///     {"handover", "lobby"}) and hand it to <see cref="PublicServerCertificate.Set" />, then again with every
    ///     BEAT answer that carries a "cert"
    /// </summary>
    void LobbyOpened(ulong lobbyId);
}

/// <summary>
///     The Steam side of the host certificate (plan P3): the keeper writes the certificate the service gave it in
///     its lobby data, every game reads it back from the lobbies it lists or joins (OfficialServer.IsOfficial)
/// </summary>
internal static class PublicServerCertificate
{
    internal const string LobbyKey = "EmpHostCert";

    // the public half of the ETWorld service key (worker secret SERVICE_KEY), 64 hex each. Empty until the service is
    // deployed (API.md section 9): until then no certificate is accepted by a Release build and the public server is
    // the dedicated PC with its RSA signature (OfficialServer.AcceptLegacySignature)
    private static readonly string[] ServiceKeysHex = [];

    private static List<byte[]>? _keys;

    // signature checks cost a few milliseconds: one per certificate text seen
    private static readonly Dictionary<string, HostCertificate?> Parsed = [];

    // the keeper's last certificate, written again in a lobby opened after it came
    private static string? _current;

    // (the world service's save depot, SaveDepot "etworld:", is the one client today)
    internal static IHostCertificateIssuer? Issuer { get; set; } = new EtWorldCertificateIssuer();

    /// <summary>
    ///     The certificate this game was last given to write in its lobby, null: none
    /// </summary>
    internal static string? Current => _current;

    internal static IReadOnlyList<byte[]> ServiceKeys
    {
        get {
            if (_keys is not null) {
                return _keys;
            }

            var keys = new List<byte[]>();
            foreach (var hex in ServiceKeysHex) {
                keys.Add(HostCertificate.FromHex(hex));
            }

#if DEBUG
            // the bench's fake ETWorld signs with its own key (dev/_tools_patched/peer_cert_check.py writes it here);
            // a Release build never reads this file
            try {
                var path = Path.Combine(Application.persistentDataPath, "ElinMP", "etworld_service_test.pub");
                if (File.Exists(path)) {
                    foreach (var line in File.ReadAllLines(path)) {
                        if (line.Trim() is { Length: 64 } hex) {
                            keys.Add(HostCertificate.FromHex(hex));
                        }
                    }
                }
            } catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) {
                EmpLog.Warning(ex, "The bench's ETWorld service key could not be read");
            }
#endif

            return _keys = keys;
        }
    }

    internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <returns>the certificate of that lobby when it is good (signature, lobby id, owner when known, time)</returns>
    internal static HostCertificate? Of(LobbyData lobby, ulong owner)
    {
        var wire = lobby[LobbyKey];
        if (string.IsNullOrEmpty(wire)) {
            return null;
        }

        if (!Parsed.TryGetValue(wire, out var cert)) {
            if (Parsed.Count > 256) {
                Parsed.Clear();
            }

            cert = HostCertificate.Parse(wire, ServiceKeys);
            Parsed[wire] = cert;
        }

        return cert is not null && cert.Accepts(lobby.SteamId.m_SteamID, owner, Now) ? cert : null;
    }

    /// <summary>
    ///     The lobby says it carries a certificate (good or not)
    /// </summary>
    internal static bool Claims(LobbyData lobby)
    {
        return !string.IsNullOrEmpty(lobby[LobbyKey]);
    }

    /// <summary>
    ///     The keeper's ETWorld client got a certificate (from POST cert or a BEAT): written in this game's lobby when
    ///     it names it. null takes it off (lease lost or released)
    /// </summary>
    internal static void Set(string? wire)
    {
        _current = wire;
        Write();
    }

    /// <summary>
    ///     This game opened its lobby (SteamNetLobbyManager.OnLobbyCreated)
    /// </summary>
    internal static void LobbyOpened(ulong lobbyId)
    {
        Write();
        try {
            Issuer?.LobbyOpened(lobbyId);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "The ETWorld client could not ask a certificate for lobby {LobbyId}", lobbyId);
        }
    }

    /// <returns>the id of the lobby this game owns, 0: none</returns>
    internal static ulong OwnLobby()
    {
        try {
            var lobby = NetSession.Instance.Lobby.Current;
            return lobby.SteamId.m_SteamID != 0 &&
                   SteamMatchmaking.GetLobbyOwner(lobby.SteamId) == SteamUser.GetSteamID()
                ? lobby.SteamId.m_SteamID
                : 0;
        } catch (Exception) {
            // (Steam not running)
            return 0;
        }
    }

    private static void Write()
    {
        if (OwnLobby() == 0) {
            return;
        }

        var lobby = NetSession.Instance.Lobby.Current;
        var me = SteamUser.GetSteamID().m_SteamID;

        if (_current is null) {
            if (Claims(lobby)) {
                lobby[LobbyKey] = "";
                EmpLog.Information("Lobby no longer certified as the Elin Together public server");
            }

            return;
        }

        // never publish a text other games would refuse: it says what went wrong here instead
        var cert = HostCertificate.Check(_current, lobby.SteamId.m_SteamID, me, Now, ServiceKeys);
        if (cert is null) {
            EmpLog.Warning("The host certificate does not fit lobby {LobbyId} (other lobby, other keeper, over, or an " +
                           "unknown service key): not written", lobby.SteamId.m_SteamID);
            return;
        }

        if (lobby[LobbyKey] == _current) {
            return;
        }

        var first = !Claims(lobby);
        lobby[LobbyKey] = _current;
        if (first) {
            EmpLog.Information("Lobby certified as the Elin Together public server, kept by {Name}, handover {Handover}",
                cert.KeeperName, cert.Handover);
        }
    }
}

/// <summary>
///     The world service's save depot (SaveDepot with an "etworld:" address) gets the certificates of its lease
/// </summary>
internal sealed class EtWorldCertificateIssuer : IHostCertificateIssuer
{
    public bool Keeps => SaveDepot.KeepsEtWorld;

    public void LobbyOpened(ulong lobbyId)
    {
        SaveDepot.AskCertificate(lobbyId);
    }
}
