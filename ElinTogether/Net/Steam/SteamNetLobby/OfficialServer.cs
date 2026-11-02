using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HeathenEngineering.SteamworksIntegration;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net.Steam;

/// <summary>
///     The project's own public game (user, 2026-10-27): the "Elin Together" Steam account hosts a public game anyone
///     can join, shown first in the list of games as the official server. A lobby's name and data are whatever its
///     host writes, and Steam tells a lobby's owner only to its members: the official game signs its lobby id with a
///     key kept on the PC that hosts it (ElinMP/official_server.key, never published), every game checks the
///     signature with the public half below. A game calling itself "Elin Together" is not shown as official.
///     Plan P3 (peer-hosted public server): the lobby of whichever player keeps the world carries a host certificate
///     from the ETWorld service instead (PublicServerCertificate); both are accepted until the dedicated PC retires (P9)
/// </summary>
internal static class OfficialServer
{
    internal const string SignatureKey = "EmpOfficial";

    // THE SWITCH between the two ways a lobby proves it is the public server. Legacy: the dedicated PC's RSA
    // signature of its lobby id, owner = the Elin Together account. Certificate: the ETWorld service's Ed25519
    // certificate naming the lobby, owner = the keeper it names. Turn the legacy one off at P9, when the dedicated
    // PC stops keeping the world (plan section 6)
    internal const bool AcceptLegacySignature = true;
    internal const bool AcceptHostCertificate = true;

    // the "Elin Together" Steam account: only it signs, so that the key alone, copied elsewhere, makes no official game
    internal const ulong Account = 76561198768728915;

    // a public game can be big: the lobby of an ordinary game holds 16
    internal const int MaxPlayers = 100;

    private const string PublicKey =
        "<RSAKeyValue><Modulus>3jmy3n0WQfj1U3Ob9h+MEB/ehwuF79byRagfstI4uynV7cdi0JwOB9wJtBNMgJlPXKthObjEZFCwhdTxMZQy+mCVTGtgKsmAiulMFK3wMRkuOwSde/mq7d9rt2tLiSVuV5Bjl1Mo59cLDR4knMkKna9zFeSx3aKVJ42lPwYWfYkxZOPvyZnnr47w6Hk0t0xJRPwHqtohqJ6Jg07Rqyrahgle8YOGtW9moz+p/kqikQeMDLC0U5m0gLdcSvnirfG6OpgrKCPjnbOPH7zqEsTEh1e1lUCvXN/A9zCI8pYY7YwB9e5/H6cbZq/lOUVZWko/xOucNroxKABIte565EEEWQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    private static string KeyPath => Path.Combine(Application.persistentDataPath, "ElinMP", "official_server.key");

    /// <summary>
    ///     This game can host the official server: the Elin Together account, with the key on this PC
    /// </summary>
    internal static bool CanHost()
    {
        try {
            return SteamUser.GetSteamID().m_SteamID == Account && File.Exists(KeyPath);
        } catch (Exception) {
            // (Steam not running)
            return false;
        }
    }

    /// <summary>
    ///     This game is the official server being open: the official server refuses imported characters (user,
    ///     2026-10-27), so ImportCharacter reads off whatever the host ticked. A hidden server (started by a player's
    ///     own visible game, -empparent) is that player's world, never the public server on its own: on the PC that
    ///     holds the key it signed its lobby as the public server and wrote the public rules over the player's config
    /// </summary>
    internal static bool Hosting => BenchForce ||
                                    (EmpConfig.Server.PublicGame.Value || EmpServer.Requested && !EmpServer.Hidden) && CanHost();

    // set by the bench through reflection: a test PC has neither the account nor the key. It only adds the official
    // server's own limits to the game that sets it (nothing is signed, see CanHost)
    internal static bool BenchForce;

    /// <returns>the signature of that lobby, null when this game cannot sign</returns>
    internal static string? Sign(ulong lobbyId)
    {
        if (!CanHost()) {
            return null;
        }

        try {
            using var rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(File.ReadAllText(KeyPath));
            return Convert.ToBase64String(rsa.SignData(Message(lobbyId), "SHA256"));
        } catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or FormatException) {
            EmpLog.Warning(ex, "The official server key could not sign the lobby");
            return null;
        }
    }

    /// <summary>
    ///     The owner of the lobby, when Steam tells it (only to its members, 0 before), is the Elin Together account:
    ///     a signature copied onto a lobby of someone else is worth nothing (council 2026-10-28, ops)
    /// </summary>
    internal static bool OwnerAllows(ulong owner)
    {
        return owner == 0 || owner == Account;
    }

    internal static bool Verify(ulong lobbyId, string? signature, ulong owner = 0)
    {
        if (string.IsNullOrEmpty(signature) || !OwnerAllows(owner)) {
            return false;
        }

        try {
            using var rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(PublicKey);
            return rsa.VerifyData(Message(lobbyId), "SHA256", Convert.FromBase64String(signature));
        } catch (Exception ex) when (ex is CryptographicException or FormatException) {
            return false;
        }
    }

    internal static bool IsOfficial(LobbyData lobby)
    {
        var owner = OwnerOf(lobby);
        return AcceptLegacySignature && Verify(lobby.SteamId.m_SteamID, lobby[SignatureKey], owner) ||
               AcceptHostCertificate && PublicServerCertificate.Of(lobby, owner) is not null;
    }

    /// <returns>the name of the player keeping the public world in that lobby, null for the dedicated PC or another game</returns>
    internal static string? KeeperName(LobbyData lobby)
    {
        return AcceptHostCertificate && PublicServerCertificate.Of(lobby, OwnerOf(lobby)) is { } cert
            ? cert.KeeperName.Replace("<", "").Replace(">", "")
            : null;
    }

    /// <summary>
    ///     The lobby says it is the public server (a signature or a certificate, good or not)
    /// </summary>
    internal static bool Claims(LobbyData lobby)
    {
        return !string.IsNullOrEmpty(lobby[SignatureKey]) || PublicServerCertificate.Claims(lobby);
    }

    private static ulong OwnerOf(LobbyData lobby)
    {
        try {
            return SteamMatchmaking.GetLobbyOwner(lobby.SteamId).m_SteamID;
        } catch (Exception) {
            return 0;
        }
    }

    // (the lobby id, so that a signature copied onto another lobby is worth nothing)
    private static byte[] Message(ulong lobbyId)
    {
        return Encoding.UTF8.GetBytes($"elin-together-official:{lobbyId}");
    }
}
