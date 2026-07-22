using System;
using System.Collections.Generic;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using HeathenEngineering.SteamworksIntegration;
using Serilog.Events;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net.Steam;

public partial class SteamNetManager
{
    public static readonly Dictionary<UserData, string> ConnectionKeys = [];

    /// <summary>
    ///     Start server on valve SDR
    /// </summary>
    public void StartServerSdr()
    {
        EmpLog.Debug("Starting relay server via SDR");

        var options = SteamNetConfig.Default.Create();
        _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
        if (_listenSocket == HSteamListenSocket.Invalid) {
            throw new InvalidOperationException("Failed to create listen socket via SDR");
        }

        SetupSteamCallback();
    }

    /// <summary>
    ///     Mainly just for debugging
    /// </summary>
    public void StartServerUdp(ushort port = EmpConstants.LocalPort)
    {
        EmpLog.Debug("Starting local udp server at port {Port}",
            port);

        var localhost = new SteamNetworkingIPAddr();
        localhost.Clear();
        localhost.m_port = port;

        var options = SteamNetConfig.Default.Create();
        _listenSocket = SteamNetworkingSockets.CreateListenSocketIP(ref localhost, options.Length, options);
        if (_listenSocket == HSteamListenSocket.Invalid) {
            throw new InvalidOperationException("Failed to create listen socket via UDP");
        }

        SetupSteamCallback();
    }

    private void AcceptIfHost(HSteamNetConnection connection, SteamNetConnectionInfo_t info)
    {
        UserData user = info.m_identityRemote.GetSteamID64();

        EmpLog.Debug("Received connection request from {RemoteIdentity}",
            user);

        var connectionKey = BuildVersionIntegrity.VersionStringToLong(ModInfo.BuildVersion);
        if (info.m_nUserData != connectionKey) {
            EmpPop.Debug("emp_connection_rejected".Loc(
                ModInfo.BuildVersion.TagColor(Color.green),
                BuildVersionIntegrity.LongToVersionString(info.m_nUserData).TagColor(Color.red)));

            // only connect if we have same build version
            SteamNetworkingSockets.CloseConnection(connection, 0, "emp_version_mismatch", false);
            return;
        }

        if (!ConnectionKeys.TryGetValue(user, out var key)) {
            // only connect if host allows it in the lobby
            SteamNetworkingSockets.CloseConnection(connection, 0, "emp_not_allowed", false);
            return;
        }

        EmpLog.Debug("Accepting connection request from {RemoteIdentity}",
            user);

        var result = SteamNetworkingSockets.AcceptConnection(connection);
        if (result != EResult.k_EResultOK) {
            EmpPop.Popup(LogEventLevel.Warning, "emp_accept_failed".lang());
        }
    }

    private void DiscardListenSocket()
    {
        if (_listenSocket != HSteamListenSocket.Invalid) {
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            _listenSocket = HSteamListenSocket.Invalid;
        }

        IsHost = false;
        IsListening = false;
    }

    private void SetupSteamCallback()
    {
        if (IsListening) {
            return;
        }

        IsHost = true;
        IsListening = true;
    }
}