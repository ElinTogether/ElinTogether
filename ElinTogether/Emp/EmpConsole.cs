using ElinTogether.Common;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using ReflexCLI.Attributes;

namespace ElinTogether;

[ConsoleCommandClassCustomizer("emp")]
internal class EmpConsole
{
    [ConsoleCommand("add_server")]
    internal static void AddServer()
    {
        var server = NetSession.Instance.InitializeComponent<ElinNetHost>();
        server.StartServer();
    }

    [ConsoleCommand("disconnect")]
    internal static void Disconnect()
    {
        NetSession.Instance.ResetSession();
    }

    [ConsoleCommand("kick")]
    internal static string KickPlayer(int playerIndex)
    {
        if (NetSession.Instance.Connection is not ElinNetHost) {
            return "Only the host can kick players";
        }

        if (playerIndex == 0) {
            return "Cannot kick the host";
        }

        NetSession.Instance.Connection.DisconnectPeer(playerIndex, EmpDisconnectInfo.HostKick);
        EmpLog.Information("Kicked player at index {Index}", playerIndex);
        return $"Kicked player {playerIndex}";
    }

    [ConsoleCommand("reconnect")]
    internal static string ReconnectPlayer(int playerIndex)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return "Only the host can request a client to reconnect";
        }

        if (playerIndex == 0) {
            return "Cannot request the host to reconnect";
        }

        host.RequestClientReconnect(playerIndex);
        EmpLog.Information("Requested reconnect for player at index {Index}", playerIndex);
        return $"Requested reconnect for player {playerIndex}";
    }

    [ConsoleCommand("reconnect_self")]
    internal static string ReconnectSelf()
    {
        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return "Only a client can manually reconnect";
        }

        client.ReconnectSelf();
        EmpLog.Information("Manual reconnect initiated");
        return "Manual reconnect initiated";
    }

    [ConsoleCommand("connect_steam")]
    internal static void AddClientToSteamId(ulong steamId64)
    {
        var client = NetSession.Instance.InitializeComponent<ElinNetClient>();
        client.ConnectSteamUser(steamId64);
    }

    [ConsoleCommand("lobby.create_public")]
    internal static void CreatePublicLobby(int maxPlayers = 16)
    {
        NetSession.Instance.Lobby.CreateLobby(SteamNetLobbyType.Public, maxPlayers);
    }

    [ConsoleCommand("lobby.invite_steam")]
    internal static void InviteSteamUser(ulong steamId64)
    {
        NetSession.Instance.Lobby.InviteSteamUser(steamId64);
    }

    [ConsoleCommand("lobby.invite_overlay")]
    internal static void InviteSteamOverlay()
    {
        NetSession.Instance.Lobby.InviteSteamOverlay();
    }

#if DEBUG
    [ConsoleCommand("add_local")]
    internal static void AddLocalServerUdp()
    {
        var server = NetSession.Instance.InitializeComponent<ElinNetHost>();
        server.StartServer(true);
    }

    [ConsoleCommand("connect_udp")]
    internal static void AddClientToUdpPort()
    {
        var client = NetSession.Instance.InitializeComponent<ElinNetClient>();
        client.ConnectLocalPort();
    }

    [ConsoleCommand("d1")]
    internal static void AddClientD1()
    {
        AddClientToSteamId(76561198412175578UL);
    }

    [ConsoleCommand("d2")]
    internal static void AddClientD2()
    {
        AddClientToSteamId(76561198254677013UL);
    }
#endif
}