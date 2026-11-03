using ElinTogether.LangMod;

namespace ElinTogether.Common;

public static class EmpDisconnectInfo
{
    public const string InvalidSource = "emp_dc_invalid_source";
    public const string InvalidZone = "emp_dc_invalid_zone";
    public const string HostShutdown = "emp_dc_host_shutdown";
    public const string JoinWhileConnected = "emp_dc_new_join_while_connected";
    public const string NetSessionInitialize = "emp_dc_net_session_initialize";
    public const string InactivePeer = "emp_dc_inactive_peer";
    public const string Timeout = "emp_dc_timeout";
    public const string NewHost = "emp_dc_new_host";
    public const string HostLeftLobby = "emp_dc_host_left_lobby";
    public const string HostKick = "emp_dc_host_kick";
    public const string HostReconnectRequest = "emp_dc_reconnect_request";
    public const string RemoteClosed = "emp_dc_remote_closed";
    public const string ClientCancel = "emp_dc_client_cancel";
    public const string VersionMismatch = "emp_dc_version_mismatch";
    public const string ActMappingMismatch = "emp_dc_act_mismatch";
    public const string Banned = "emp_dc_banned";
    public const string KickWait = "emp_dc_kick_wait";
    public const string MembersOnly = "emp_dc_members_only";
    public const string OwnerSeat = "emp_dc_owner_seat";
    public const string PeerRules = "emp_dc_peer_rules";
    public const string ServerFull = "emp_dc_server_full";

    /// <summary>
    ///     A reason with words of its own (the host's, a count): "key|words", told as "key_why" with them. Steam takes
    ///     128 characters at most
    /// </summary>
    public static string With(string key, string? words)
    {
        words = words?.Replace('|', ' ').Trim();
        if (string.IsNullOrEmpty(words)) {
            return key;
        }

        return key + "|" + (words!.Length > 80 ? words.Substring(0, 80) : words);
    }

    /// <summary>
    ///     The link dropped without anyone deciding it (dead link, no answer, closed without a word): the player
    ///     did not leave and was not turned away, see NetReconnect
    /// </summary>
    public static bool IsLinkLost(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason is Timeout or InactivePeer or RemoteClosed) {
            return true;
        }

        // every reason of ours is a word of this class, or the refusal of a version ("emp_version_mismatch|..."):
        // anything else is Steam's own word for a link it gave up on ("Connection dropped", seen in a real game
        // on 2026-10-09: the guest was left on the title screen)
        return !reason!.StartsWith("emp_", System.StringComparison.Ordinal);
    }

    public static string Describe(string? reason)
    {
        if (BuildVersionIntegrity.GetGtfoReason(reason, out var mod, out var game)) {
            return "emp_version_rejected_client".Loc(BuildVersionIntegrity.Explain(mod, game));
        }

        if (reason?.IndexOf('|') is > 0 and var bar && reason.StartsWith("emp_dc_", System.StringComparison.Ordinal)) {
            return (reason.Substring(0, bar) + "_why").Loc(reason.Substring(bar + 1));
        }

        return (reason ?? RemoteClosed).lang();
    }
}