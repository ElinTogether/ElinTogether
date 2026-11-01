using System;
using System.Collections.Generic;
using System.Text;

namespace ElinTogether.Net.Steam;

/// <summary>
///     The host certificate of the Elin Together public server (plan P3, dev/etworld/API.md section 4.1): the ETWorld
///     service gives the player keeping the world (its lease holder) a short text naming its Steam lobby, signed by
///     the service key; the keeper writes it in its lobby data and every game checks it with the public half built
///     into the mod. A keeper never holds a key that makes certificates.
///     Wire form (lobby data <see cref="PublicServerCertificate.LobbyKey" />): "ETC1.&lt;base64 of the text&gt;.&lt;128 hex
///     Ed25519 signature of the text&gt;"; the text is 8 lines joined by "\n":
///     ETCERT1, world, lobby id, keeper Steam id, keeper ETWorld key, handover, not-after (Unix s), keeper name.
///     This file has no Steam or Unity code (checked outside the game, see dev/_tools_patched/peer_cert_check.py)
/// </summary>
internal sealed class HostCertificate
{
    internal const string Tag = "ETC1";
    internal const string World = "public";

    // the players' clocks are not the service's: a certificate stays good this long after its end (the service's own
    // signed requests allow the same 120 s)
    internal const long ClockAllowance = 120;

    internal string WorldId = "";
    internal ulong Lobby;
    internal ulong Keeper;
    internal string KeeperKey = "";
    internal long Handover;
    internal long NotAfter;
    internal string KeeperName = "";

    /// <returns>the certificate when it is good for that lobby, owner and time, null otherwise</returns>
    /// <param name="owner">the lobby owner, 0 when Steam does not tell it (only members learn a lobby's owner)</param>
    internal static HostCertificate? Check(string? wire, ulong lobbyId, ulong owner, long now,
        IReadOnlyList<byte[]> serviceKeys)
    {
        return Parse(wire, serviceKeys) is { } cert && cert.Accepts(lobbyId, owner, now) ? cert : null;
    }

    /// <summary>
    ///     A certificate whose signature is good names this lobby, this owner (when known) and is not over
    /// </summary>
    internal bool Accepts(ulong lobbyId, ulong owner, long now)
    {
        if (WorldId != World || Lobby != lobbyId || lobbyId == 0) {
            return false;
        }

        if (owner != 0 && owner != Keeper) {
            return false;
        }

        return now <= NotAfter + ClockAllowance;
    }

    /// <returns>the fields of a certificate whose signature is good, whatever lobby or time it names</returns>
    internal static HostCertificate? Parse(string? wire, IReadOnlyList<byte[]> serviceKeys)
    {
        if (string.IsNullOrEmpty(wire) || wire!.Length > 2048 || serviceKeys.Count == 0) {
            return null;
        }

        var parts = wire.Split('.');
        if (parts.Length != 3 || parts[0] != Tag || parts[2].Length != 128) {
            return null;
        }

        byte[] payload;
        byte[] signature;
        try {
            payload = Convert.FromBase64String(parts[1]);
            signature = FromHex(parts[2]);
        } catch (FormatException) {
            return null;
        }

        var signed = false;
        foreach (var key in serviceKeys) {
            if (Ed25519Verifier.Verify(key, payload, signature)) {
                signed = true;
                break;
            }
        }

        if (!signed) {
            return null;
        }

        string[] lines;
        try {
            lines = new UTF8Encoding(false, true).GetString(payload).Split('\n');
        } catch (ArgumentException) {
            return null;
        }

        if (lines.Length != 8 || lines[0] != "ETCERT1" ||
            !ulong.TryParse(lines[2], out var lobby) ||
            !ulong.TryParse(lines[3], out var keeper) ||
            !long.TryParse(lines[5], out var handover) ||
            !long.TryParse(lines[6], out var notAfter)) {
            return null;
        }

        return new() {
            WorldId = lines[1],
            Lobby = lobby,
            Keeper = keeper,
            KeeperKey = lines[4],
            Handover = handover,
            NotAfter = notAfter,
            KeeperName = lines[7],
        };
    }

    internal static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0) {
            throw new FormatException("odd hex");
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++) {
            bytes[i] = (byte)(Nibble(hex[2 * i]) << 4 | Nibble(hex[2 * i + 1]));
        }

        return bytes;
    }

    private static int Nibble(char c)
    {
        return c switch {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new FormatException("not hex"),
        };
    }
}
