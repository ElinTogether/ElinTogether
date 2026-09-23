using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class MsgRelayContext
{
    private static Mode _mode;
    private static int _peerIndex;

    internal static bool IsRedirecting => _mode == Mode.Redirect;

    internal static ScopeExit RedirectTo(Chara receiver)
    {
        return NetSession.Instance.Connection is ElinNetHost host
            ? RedirectTo(host.ActiveRemoteCharas.FirstOrDefault(pair => pair.Value == receiver).Key)
            : new();
    }

    internal static ScopeExit RedirectTo(int peerIndex)
    {
        return peerIndex == 0 ? new() : Enter(Mode.Redirect, peerIndex);
    }

    internal static ScopeExit Suppress(bool active = true)
    {
        return active ? Enter(Mode.Suppress, 0) : new();
    }

    private static ScopeExit Enter(Mode mode, int peerIndex)
    {
        var (previousMode, previousPeer) = (_mode, _peerIndex);
        _mode = mode;
        _peerIndex = peerIndex;
        return new() {
            OnExit = () => {
                _mode = previousMode;
                _peerIndex = previousPeer;
            },
        };
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Msg), nameof(Msg.SayRaw))]
    internal static bool OnSayRaw(string text, ref string __result)
    {
        if (_mode == Mode.None || Msg.ignoreAll || NetSession.Instance.Connection is not ElinNetHost host) {
            return true;
        }

        var color = Msg.currentColor;
        Msg.SetColor();
        Msg.alwaysVisible = false;
        __result = text;

        if (_mode == Mode.Redirect) {
            host.SendDeltaTo(_peerIndex, new MsgSayDelta {
                Text = text,
                R = color.r,
                G = color.g,
                B = color.b,
                A = color.a,
            });
        }

        return false;
    }

    private enum Mode : byte
    {
        None,
        Redirect,
        Suppress,
    }
}