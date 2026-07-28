using System;
using UnityEngine;

namespace ElinTogether.Net;

internal static class NetShutdown
{
    internal static bool IsQuitting { get; private set; }

    internal static void SetupApplicationHook()
    {
        Application.wantsToQuit -= OnWantsToQuit;
        Application.wantsToQuit += OnWantsToQuit;
    }

    internal static void Shutdown()
    {
        if (IsQuitting) {
            return;
        }

        IsQuitting = true;

        try {
            NetSession.Instance.Connection?.Shutdown();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Exception while closing sockets on quit");
            // noexcept
        }

        try {
            NetSession.Instance.Lobby.Shutdown();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Exception while leaving lobby on quit");
            // noexcept
        }
    }

    private static bool OnWantsToQuit()
    {
        Shutdown();
        return true;
    }
}