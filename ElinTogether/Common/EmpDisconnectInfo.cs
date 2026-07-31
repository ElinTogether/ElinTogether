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

    public static string Describe(string? reason)
    {
        if (BuildVersionIntegrity.GetGtfoReason(reason, out var mod, out var game)) {
            return "emp_version_rejected_client".Loc(ModInfo.BuildVersion, mod, BuildVersionIntegrity.GameVersion, game);
        }

        return (reason ?? RemoteClosed).lang();
    }
}