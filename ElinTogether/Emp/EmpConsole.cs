using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using ReflexCLI.Attributes;
#if DEBUG
using System;
using System.Collections;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;
#endif

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
        ElinNetHost.LeaveWithLastCopy(true, NetSession.Instance.ResetSession);
    }

    [ConsoleCommand("kick")]
    internal static string KickPlayer(int playerIndex, string reason = "")
    {
        if (NetSession.Instance.Transport is not ElinNetHost host) {
            return "Only the host can kick players";
        }

        if (playerIndex == 0) {
            return "Cannot kick the host";
        }

        return host.Kick(playerIndex, reason);
    }

    [ConsoleCommand("ban")]
    internal static string BanPlayer(ulong steamId, string reason = "")
    {
        return NetSession.Instance.Transport is ElinNetHost host ? host.Ban(steamId, reason) : "Only the host can ban players";
    }

    [ConsoleCommand("mute")]
    internal static string MutePlayer(ulong steamId, string reason = "")
    {
        return NetSession.Instance.Transport is ElinNetHost host ? host.Mute(steamId, reason) : "Only the host can mute players";
    }

    [ConsoleCommand("unmute")]
    internal static string UnmutePlayer(ulong steamId)
    {
        return NetSession.Instance.Transport is ElinNetHost host ? host.Unmute(steamId) : "Only the host can unmute players";
    }

    [ConsoleCommand("admin")]
    internal static string AdminPlayer(ulong steamId, int on = 1)
    {
        return NetSession.Instance.Transport is ElinNetHost host ? host.MakeAdmin(steamId, on != 0) : "Only the host makes admins";
    }

    [ConsoleCommand("allow")]
    internal static string AllowPlayer(ulong steamId)
    {
        return NetSession.Instance.Transport is ElinNetHost ? ElinNetHost.Allow(steamId) : "Only the host lets players in";
    }

    [ConsoleCommand("unban")]
    internal static string UnbanPlayer(ulong steamId)
    {
        return NetSession.Instance.Transport is ElinNetHost ? ElinNetHost.Unban(steamId) : "Only the host can unban players";
    }

    [ConsoleCommand("reconnect")]
    internal static string ReconnectPlayer(int playerIndex)
    {
        if (NetSession.Instance.Transport is not ElinNetHost host) {
            return "Only the host can request a client to reconnect";
        }

        if (playerIndex == 0) {
            return "Cannot request the host to reconnect";
        }

        host.RequestClientReconnect(playerIndex);
        EmpLog.Information("Requested reconnect for player at index {PeerIndex}", playerIndex);
        return $"Requested reconnect for player {playerIndex}";
    }

    [ConsoleCommand("reconnect_self")]
    internal static string ReconnectSelf()
    {
        if (NetSession.Instance.Transport is not ElinNetClient client) {
            return "Only a client can manually reconnect";
        }

        client.ReconnectSelf();
        EmpLog.Information("Manual reconnect initiated");
        return "Manual reconnect initiated";
    }

    /// <summary>
    ///     The map checksum of this game, the last one compared with the game that keeps the map, the last warning
    /// </summary>
    [ConsoleCommand("desync")]
    internal static string Desync()
    {
        return NetDesync.Describe();
    }

    /// <summary>
    ///     Network and main-thread counters since the game started (the CSV in ElinMP/Logs has them per 10 s)
    /// </summary>
    [ConsoleCommand("metrics")]
    internal static string Metrics()
    {
        return NetMetrics.Describe();
    }

    /// <summary>
    ///     Bench: the teams as the host keeps them (or as this guest was told). The menus come later
    /// </summary>
    [ConsoleCommand("team.list")]
    internal static string TeamList()
    {
        return Teams.Describe();
    }

    /// <summary>
    ///     Bench: the world's bank and each owner's boxes (host option "Teams keep their own bank and delivery box")
    /// </summary>
    [ConsoleCommand("team.boxes")]
    internal static string TeamBoxLedger()
    {
        return TeamBoxes.Describe();
    }

    /// <summary>
    ///     Bench: what each team knows in the host's ledger (host option "Teams learn on their own")
    /// </summary>
    [ConsoleCommand("team.lore")]
    internal static string TeamLore()
    {
        return TeamKnowledge.Describe();
    }

    /// <summary>
    ///     Bench: where each team stands in the story in the host's ledger (host option "Teams live their own story")
    /// </summary>
    [ConsoleCommand("team.story")]
    internal static string TeamStoryLedger()
    {
        return TeamOwnStory.Describe();
    }

    /// <summary>
    ///     The host's explicit choice of the world owner: whose story the save keeps (a team id, or minus a chara uid)
    /// </summary>
    [ConsoleCommand("team.story_world")]
    internal static string TeamStoryWorld(int teamId)
    {
        return TeamOwnStory.SetWorldOwner(teamId);
    }

    [ConsoleCommand("team.found")]
    internal static string TeamFound(int charaUid, string name)
    {
        return PlayerOf(charaUid) is { } chara && Teams.Found(chara, name) is var id and > 0
            ? $"Team {id} founded"
            : "Not founded (host only, chara on this map, a name)";
    }

    [ConsoleCommand("team.join")]
    internal static string TeamJoin(int charaUid, int teamId)
    {
        return PlayerOf(charaUid) is { } chara && Teams.Join(chara, teamId) ? $"Joined team {teamId}" : "Not joined";
    }

    /// <summary>
    ///     Bench: what the team menus do for the local player (Found, Join, Leave, Accept, Refuse), asked of the host
    ///     from a guest's game
    /// </summary>
    [ConsoleCommand("team.ask")]
    internal static string TeamAsk(string kind, int id)
    {
        if (!System.Enum.TryParse<Models.TeamAsk>(kind, true, out var ask)) {
            return "Unknown ask";
        }

        Teams.Ask(ask, id);
        return $"Asked {ask} {id}";
    }

    [ConsoleCommand("team.leave")]
    internal static string TeamLeave(int charaUid)
    {
        return PlayerOf(charaUid) is { } chara && Teams.Leave(chara) ? "Left" : "Not in a team";
    }

    private static Chara? PlayerOf(int charaUid)
    {
        return EClass.pc?.uid == charaUid ? EClass.pc : EClass._map?.charas.Find(c => c.uid == charaUid);
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

    [ConsoleCommand("lobby.join")]
    internal static void JoinSteamLobby(ulong steamId64)
    {
        NetSession.Instance.Lobby.ConnectLobby(steamId64);
    }

#if DEBUG
    [ConsoleCommand("add_local")]
    internal static void AddLocalServerUdp()
    {
        var server = NetSession.Instance.InitializeComponent<ElinNetHost>();
        server.StartServer(true);
    }

    /// <summary>
    ///     Bench: as a Steam lobby join does, the client exists first and connects only some seconds later (issue #24)
    /// </summary>
    [ConsoleCommand("connect_udp_after")]
    internal static string AddClientToUdpPortAfter(int seconds = 5)
    {
        NetSession.Instance.InitializeComponent<ElinNetClient>();
        EmpMod.Instance.StartCoroutine(Later());
        return $"Connecting in {seconds}s";

        IEnumerator Later()
        {
            yield return new WaitForSecondsRealtime(seconds);
            (NetSession.Instance.Transport as ElinNetClient)?.ConnectLocalPort();
        }
    }

    [ConsoleCommand("connect_udp")]
    internal static void AddClientToUdpPort()
    {
        var client = NetSession.Instance.InitializeComponent<ElinNetClient>();
        client.ConnectLocalPort();
    }

    /// <summary>
    ///     Bench: the link of this game drops as on a network failure, for that many seconds. Nothing gets
    ///     through either way and nobody is told, each side finds out by itself (Steam's own fake packet loss)
    /// </summary>
    [ConsoleCommand("cut_link")]
    internal static string CutLink(int seconds = 20)
    {
        ElinNetClient.UseTimeout = true;
        SetPacketLoss(100f);
        EmpMod.Instance.StartCoroutine(Restore());
        return $"Link cut for {seconds}s";

        IEnumerator Restore()
        {
            yield return new WaitForSecondsRealtime(seconds);
            SetPacketLoss(0f);
            EmpLog.Information("Link back after {Seconds}s", seconds);
        }
    }

    /// <summary>
    ///     Bench: a dead link ends the session after the timeout as in a release build, a debug build waits for good
    /// </summary>
    [ConsoleCommand("slow_start")]
    internal static string SlowStart(int ms = 20000)
    {
        ElinNetBase.SlowStartMs = ms;
        return $"Next connection components start {ms} ms late";
    }

    [ConsoleCommand("link_timeout")]
    internal static string LinkTimeout(int on = 1)
    {
        ElinNetClient.UseTimeout = on != 0;
        return $"Link timeout {(on != 0 ? "as in a release build" : "off")}";
    }

    /// <summary>
    ///     Bench: the world is saved by itself that often instead of every 2 minutes, 0 for the real pace
    /// </summary>
    [ConsoleCommand("autosave_every")]
    internal static string AutosaveEvery(int seconds = 0)
    {
        EmpAutoHost.SaveEvery(seconds);
        return seconds > 0 ? $"Autosave every {seconds}s" : "Autosave at its real pace";
    }

    /// <summary>
    ///     Bench: a world loaded in this window opens its session by itself, on the local port (see EmpAutoHost)
    /// </summary>
    [ConsoleCommand("auto_open")]
    internal static string AutoOpen(int on = 1)
    {
        EmpAutoHost.BenchOpens = on != 0;
        return $"Session opened by itself at load: {(on != 0 ? "on" : "off")}";
    }

    /// <summary>
    ///     Bench: this guest opens the world it was playing in from the copy it keeps of it (see WorldTakeover)
    /// </summary>
    [ConsoleCommand("take_over")]
    internal static string TakeOver()
    {
        return WorldTakeover.Begin() is { Length: > 0 } why ? $"Not taken over: {why}" : "Taking the world over";
    }

    /// <summary>
    ///     Bench, host: the rule "another player takes the world over when the host leaves", told to the guests
    /// </summary>
    [ConsoleCommand("takeover_rule")]
    internal static string TakeoverRule(int on = 1)
    {
        EmpConfig.Server.Takeover.Value = on != 0;
        (NetSession.Instance.Transport as ElinNetHost)?.UpdateRemoteSessionRules();
        return $"Takeover when the host leaves: {(on != 0 ? "on" : "off")}";
    }

    /// <summary>
    ///     Bench, host: the links of the guests drop the way Steam drops them by itself
    /// </summary>
    [ConsoleCommand("drop_links")]
    internal static string DropLinks()
    {
        return NetSession.Instance.Transport is ElinNetHost host
            ? $"Dropped {host.DropLinksAsSteam()} link(s) as Steam does"
            : "Only the host drops links";
    }

    /// <summary>
    ///     Bench: a bad network for this game, both ways, with Steam's own fake packet settings (global, every link of
    ///     this process): lag in ms added to each way, loss and reorder in percent, reordered packets late by
    ///     reorderMs. emp.net_fault 0 0 0 puts it back
    /// </summary>
    [ConsoleCommand("net_fault")]
    internal static string NetFault(int lagMs = 0, float lossPercent = 0f, float reorderPercent = 0f, int reorderMs = 50)
    {
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Send, lagMs);
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Recv, lagMs);
        SetPacketLoss(lossPercent);
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Send, reorderPercent);
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Recv, reorderPercent);
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Time, reorderMs);
        EmpLog.Information("Bench network fault: lag {Lag} ms each way, loss {Loss}%, reorder {Reorder}% by {ReorderMs} ms",
            lagMs, lossPercent, reorderPercent, reorderMs);
        return $"Network fault: lag {lagMs} ms each way, loss {lossPercent}%, reorder {reorderPercent}% by {reorderMs} ms";
    }

    /// <summary>
    ///     Bench: this game's main thread stops for that long on the next frame, as a game hanging on a big save or a
    ///     slow disk (Steam's own thread keeps the links up meanwhile)
    /// </summary>
    [ConsoleCommand("freeze")]
    internal static string Freeze(int ms = 8000)
    {
        EmpMod.Instance.StartCoroutine(Later());
        return $"Freezing for {ms} ms on the next frame";

        IEnumerator Later()
        {
            yield return null;
            EmpLog.Information("Bench freeze: {Ms} ms", ms);
            System.Threading.Thread.Sleep(ms);
            EmpLog.Information("Bench freeze over");
        }
    }

    private static void SetPacketLoss(float percent)
    {
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Send, percent);
        SetFake(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Recv, percent);
    }

    private static void SetFake(ESteamNetworkingConfigValue config, float data)
    {
        SetFake(config, BitConverter.ToInt32(BitConverter.GetBytes(data), 0),
            ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float);
    }

    private static void SetFake(ESteamNetworkingConfigValue config, int data,
        ESteamNetworkingConfigDataType type = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32)
    {
        // (a float and an int32 are the same 4 bytes here)
        var value = Marshal.AllocHGlobal(sizeof(int));
        try {
            Marshal.WriteInt32(value, data);
            if (!SteamNetworkingUtils.SetConfigValue(config, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero, type, value)) {
                EmpLog.Warning("Steam refused the bench network setting {Config}", config);
            }
        } finally {
            Marshal.FreeHGlobal(value);
        }
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